using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;

namespace SecurePolicyRag.Tests;

public sealed class AzureAdapterTests
{
    [Fact]
    public async Task Hybrid_search_always_sends_prefiltered_server_entitlements()
    {
        var handler = new StubHandler("""{"value":[]}""");
        var provider = new AzurePolicySearch(new HttpClient(handler), new StubCredential(), Settings());
        var metrics = new RunMetrics("azure-test", "Azure");
        var result = await provider.SearchAsync(
            new("employee", "Test", ["employees"]), "access review", null, [.1f, .2f], metrics, default);
        Assert.Empty(result);
        Assert.Equal("https://search.example/indexes/policy-chunks/docs/search?api-version=2024-07-01", handler.Uri);
        Assert.Equal("Bearer", handler.Scheme);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.Equal("preFilter", request.RootElement.GetProperty("vectorFilterMode").GetString());
        Assert.Equal("(allowedGroups/any(g: g eq 'employees'))", request.RootElement.GetProperty("filter").GetString());
        Assert.Equal(1, metrics.SearchCalls);
    }

    [Fact]
    public async Task Free_text_question_defense_in_depth_blocks_restricted_content_even_if_search_misbehaves()
    {
        var catalog = new DocumentAuthorizationService([
            new("employee-security", "Employee Security Basics (fictional)", "v1", ["employees"],
                [new("employee-security-1", "Employees must use multifactor authentication for remote access.")]),
            new("access-review", "Privileged Access Review Policy (fictional)", "v1", ["security-reviewers"],
                [new("access-review-1",
                    "Privileged access must be reviewed every 90 days by a reviewer independent of the access owner.")])
        ]);
        var employee = new Caller("alex", "Employee", ["employees"]);
        // A real Azure AI Search service enforces the prefilter server-side; this stub instead
        // simulates a misbehaving response that ignores the filter, to prove the app's own
        // defense-in-depth (re-validation before generation) still blocks restricted content.
        var handler = new StubHandler("""
            {"value":[{"id":"access-review-1","documentId":"access-review","title":"Privileged Access Review Policy (fictional)","version":"v1","content":"Privileged access must be reviewed every 90 days by a reviewer independent of the access owner.","kind":"Policy"}]}
            """);
        var search = new AzurePolicySearch(new HttpClient(handler), new StubCredential(), Settings());
        var recorder = new WorkflowTests.RecordingProvider();
        var orchestration = new RagOrchestrationService(
            catalog, recorder, search, recorder, new AnswerValidationService(),
            new ReviewStore(new(), TimeProvider.System), TimeProvider.System);
        var metrics = new RunMetrics("azure-implicit", "Azure");
        var error = await Assert.ThrowsAsync<WorkflowException>(() => orchestration.AskAsync(
            employee, new("What are the privileged access review requirements?"), metrics, default));
        Assert.Equal(403, error.StatusCode);
        Assert.Equal(0, recorder.GenerationCount);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.Equal("(allowedGroups/any(g: g eq 'employees'))", request.RootElement.GetProperty("filter").GetString());
    }

    [Fact]
    public async Task Embedding_dimension_mismatch_is_an_explicit_failure()
    {
        var provider = Provider(new StubHandler("""{"data":[{"embedding":[0.1]}]}"""));
        var metrics = new RunMetrics("embedding", "Azure");
        var error = await Assert.ThrowsAsync<WorkflowException>(() => provider.EmbedAsync("question", metrics, default));
        Assert.Equal("invalid_embedding", error.Code);
        Assert.Equal(1, metrics.EmbeddingCalls);
    }

    [Theory]
    [InlineData("""{"data":[{"embedding":[0,0]}]}""")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"unrelated":true}""")]
    public async Task Malformed_embedding_is_not_a_success(string body)
    {
        var error = await Assert.ThrowsAsync<WorkflowException>(() =>
            Provider(new StubHandler(body)).EmbedAsync("question", new("embedding", "Azure"), default));
        Assert.Equal(502, error.StatusCode);
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("Anthropic")]
    public async Task Generation_supports_explicit_gateway_protocols(string protocol)
    {
        const string answer = """{"status":"answered","excerpts":[{"chunkId":"p-1","quote":"A valid quoted policy passage."}]}""";
        var body = protocol == "OpenAI"
            ? JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer } } } })
            : JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = answer } } });
        var handler = new StubHandler(body);
        var metrics = new RunMetrics("generation", "Azure");
        var provider = Provider(handler, protocol);
        var result = await provider.GenerateAsync(
            "question", [new("p-1", "p", "Policy", "v1", "A valid quoted policy passage.", "Policy")],
            metrics, default);
        Assert.Equal("answered", result.Status);
        Assert.Equal("p-1", result.Excerpts[0].ChunkId);
        Assert.Equal(1, metrics.GenerationCalls);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.Equal("approved-answer-alias", request.RootElement.GetProperty("model").GetString());
        if (protocol == "Anthropic")
        {
            Assert.True(request.RootElement.TryGetProperty("system", out _));
            Assert.Equal("2023-06-01", handler.AnthropicVersion);
        }
        else Assert.Equal("system", request.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Upstream_error_body_is_not_exposed_and_no_retry_or_fallback_occurs()
    {
        var handler = new StubHandler("private-provider-diagnostics", HttpStatusCode.Forbidden);
        var error = await Assert.ThrowsAsync<WorkflowException>(() =>
            Provider(handler).EmbedAsync("question", new("denied", "Azure"), default));
        Assert.Equal("upstream_rejected", error.Code);
        Assert.DoesNotContain("private-provider-diagnostics", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Invalid_model_json_is_an_explicit_error()
    {
        var body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "Not JSON" } } } });
        var error = await Assert.ThrowsAsync<WorkflowException>(() => Provider(new StubHandler(body))
            .GenerateAsync("question", [], new("invalid", "Azure"), default));
        Assert.Equal("invalid_generation_response", error.Code);
    }

    [Fact]
    public void Azure_startup_rejects_non_https_and_cross_host_relative_paths()
    {
        var settings = Settings();
        settings.TenantId = Guid.NewGuid().ToString();
        settings.Audience = "api://poc";
        StartupValidation.Validate(new() { Mode = "Azure" }, false, settings);
        settings.GenerationPath = "https://untrusted.example/messages";
        Assert.Throws<InvalidOperationException>(() =>
            StartupValidation.Validate(new() { Mode = "Azure" }, false, settings));
    }

    [Fact]
    public void Indexer_requires_azure_but_not_runtime_user_or_answer_settings()
    {
        var settings = Settings();
        settings.AnswerModel = "";
        StartupValidation.ValidateIndexer(new() { Mode = "Azure" }, settings);
        Assert.Throws<InvalidOperationException>(() => StartupValidation.ValidateIndexer(new(), settings));
        Assert.Throws<InvalidOperationException>(() =>
            StartupValidation.Validate(new() { Mode = "Azure" }, false, settings));
        settings.ApimEndpoint = "";
        Assert.Throws<InvalidOperationException>(() =>
            StartupValidation.ValidateIndexer(new() { Mode = "Azure" }, settings));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Standalone_ingestion_preserves_schema_vectors_permissions_and_checks_results(bool uploadSucceeds)
    {
        var settings = Settings();
        var handler = new IndexingHandler(uploadSucceeds);
        using var http = new HttpClient(handler);
        var credential = new StubCredential();
        var models = new ApimModelClient(http, credential, settings);
        var indexer = new AzurePolicyIndexer(http, credential, settings, models);
        var catalog = new DocumentAuthorizationService([
            new("p", "Policy", "v1", ["employees"],
                [new("p-1", "Employees must use multifactor authentication for remote access.")])
        ]);
        if (uploadSucceeds)
            await indexer.IngestAsync(catalog, default);
        else
            Assert.Equal("indexing_failed", (await Assert.ThrowsAsync<WorkflowException>(
                () => indexer.IngestAsync(catalog, default))).Code);

        Assert.Equal(3, handler.Requests.Count);
        var schema = handler.Requests[0];
        Assert.Equal(HttpMethod.Put, schema.Method);
        using var index = JsonDocument.Parse(schema.Body);
        Assert.Contains(index.RootElement.GetProperty("fields").EnumerateArray(),
            f => f.GetProperty("name").GetString() == "allowedGroups" && f.GetProperty("filterable").GetBoolean());
        Assert.Contains(index.RootElement.GetProperty("fields").EnumerateArray(),
            f => f.GetProperty("name").GetString() == "vector" && f.GetProperty("dimensions").GetInt32() == 2);
        Assert.Equal("gateway.example", handler.Requests[1].Uri.Host);
        using var embedding = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("approved-embedding-alias", embedding.RootElement.GetProperty("model").GetString());
        using var uploaded = JsonDocument.Parse(handler.Requests[2].Body);
        var chunk = uploaded.RootElement.GetProperty("value")[0];
        Assert.Equal("p-1", chunk.GetProperty("id").GetString());
        Assert.Equal("employees", chunk.GetProperty("allowedGroups")[0].GetString());
        Assert.Equal(2, chunk.GetProperty("vector").GetArrayLength());
        Assert.All(handler.Requests, r => Assert.Equal("Bearer", r.Scheme));
    }

    private static AzureOptions Settings(string protocol = "OpenAI") => new()
    {
        ApimEndpoint = "https://gateway.example/",
        ApimScope = "api://model-gateway/.default",
        SearchEndpoint = "https://search.example",
        SearchIndex = "policy-chunks",
        EmbeddingModel = "approved-embedding-alias",
        AnswerModel = "approved-answer-alias",
        EmbeddingDimensions = 2,
        GenerationProtocol = protocol
    };

    private static ApimModelClient Provider(StubHandler handler, string protocol = "OpenAI") =>
        new(new HttpClient(handler), new StubCredential(), Settings(protocol));

    private sealed class StubCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("fixture-access-token-not-a-real-credential", DateTimeOffset.UtcNow.AddMinutes(5));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string Body, string? Scheme);

    private sealed class IndexingHandler(bool uploadSucceeds) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method, request.RequestUri!,
                await request.Content!.ReadAsStringAsync(cancellationToken), request.Headers.Authorization?.Scheme));
            var body = request.Method == HttpMethod.Put ? "{}"
                : request.RequestUri!.Host == "gateway.example" ? """{"data":[{"embedding":[0.1,0.2]}]}"""
                : JsonSerializer.Serialize(new { value = new[] { new { key = "p-1", status = uploadSucceeds } } });
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? Uri { get; private set; }
        public string? Scheme { get; private set; }
        public string? AnthropicVersion { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Uri = request.RequestUri!.ToString();
            Scheme = request.Headers.Authorization?.Scheme;
            AnthropicVersion = request.Headers.TryGetValues("anthropic-version", out var versions) ? versions.Single() : null;
            return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
