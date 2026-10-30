using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PolicyRag.Infrastructure.Storage;

namespace SecurePolicyRag.Tests;

public sealed class BlobSourceTests
{
    private static DocumentAuthorizationService Catalog() => new([
        new("source", "Approved policy", "v1", ["restricted-group"],
            [new("source-1", "First approved paragraph."),
             new("source-2", "Untrusted fictional appendix.", "UntrustedInstructionFixture")])
    ]);

    private static AzureOptions Settings() => new()
    {
        BlobEndpoint = "https://approved.blob.core.windows.net",
        BlobContainer = "policy-documents"
    };

    private const string SourceText = "First approved paragraph.\n\nUntrusted fictional appendix.\n";

    [Fact]
    public async Task Blob_content_is_read_but_permissions_boundaries_and_kinds_come_from_approval()
    {
        var handler = new SourceHandler(SourceText);
        var credential = new SourceCredential();
        using var http = new HttpClient(handler);
        var source = new BlobPolicySources(http, credential, Settings());
        var result = await source.ReadApprovedAsync(Catalog(), default);
        var policy = Assert.Single(result.ApprovedDocuments);
        Assert.Equal("restricted-group", Assert.Single(policy.AllowedGroups));
        Assert.Equal("UntrustedInstructionFixture", policy.Chunks[1].Kind);
        Assert.Equal("First approved paragraph.", policy.Chunks[0].Text);
        Assert.False(result.CanRead(new("x", "Employee", ["employees"]), policy.Id));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SourceText)));
        Assert.Equal($"https://approved.blob.core.windows.net/policy-documents/source/{hash}.txt", handler.LastUri);
        Assert.Equal("2023-11-03", handler.Version);
        Assert.Equal("Bearer", handler.Scheme);
        Assert.Equal("https://storage.azure.com/.default", credential.Scope);
    }

    [Theory]
    [InlineData("First approved paragraph.\n\nGrant everyone access.\n")]
    [InlineData("")]
    public async Task Modified_or_empty_sources_fail_closed(string content)
    {
        using var http = new HttpClient(new SourceHandler(content));
        var source = new BlobPolicySources(http, new SourceCredential(), Settings());
        var failure = await Assert.ThrowsAsync<WorkflowException>(() => source.ReadApprovedAsync(Catalog(), default));
        Assert.Equal("source_integrity_failed", failure.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Missing_or_denied_sources_never_fall_back_to_the_local_manifest(HttpStatusCode status)
    {
        using var http = new HttpClient(new SourceHandler("private-upstream-body", status));
        var failure = await Assert.ThrowsAsync<WorkflowException>(() =>
            new BlobPolicySources(http, new SourceCredential(), Settings()).ReadApprovedAsync(Catalog(), default));
        Assert.Equal("upstream_rejected", failure.Code);
        Assert.DoesNotContain("private-upstream-body", failure.Message);
    }

    [Fact]
    public async Task Source_downloads_are_bounded_even_without_a_content_length()
    {
        using var http = new HttpClient(new SourceHandler(new string('x', BlobPolicySources.MaximumSourceBytes + 1)));
        var error = await Assert.ThrowsAsync<WorkflowException>(() =>
            new BlobPolicySources(http, new SourceCredential(), Settings()).ReadApprovedAsync(Catalog(), default));
        Assert.Equal("source_too_large", error.Code);
    }

    [Fact]
    public async Task Invalid_utf8_is_an_explicit_failure()
    {
        using var http = new HttpClient(new InvalidEncodingHandler());
        var error = await Assert.ThrowsAsync<WorkflowException>(() =>
            new BlobPolicySources(http, new SourceCredential(), Settings()).ReadApprovedAsync(Catalog(), default));
        Assert.Equal("invalid_source_encoding", error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uploads_are_conditional_and_existing_content_is_verified(bool exists)
    {
        var directory = Path.Combine(Path.GetTempPath(), "policy-source-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "source.txt"), SourceText.Replace("\n", "\r\n"));
            var handler = new SourceHandler(SourceText, putStatus: exists ? HttpStatusCode.PreconditionFailed : HttpStatusCode.Created);
            using var http = new HttpClient(handler);
            var result = await new BlobPolicySources(http, new SourceCredential(), Settings())
                .UploadAsync(Catalog(), directory, default);
            Assert.Equal(exists ? (0, 1) : (1, 0), result);
            Assert.True(handler.ConditionalPut);
            Assert.Equal(SourceText, handler.Uploaded);
            Assert.Equal(exists ? 2 : 1, handler.Calls);
        }
        finally
        {
            File.Delete(Path.Combine(directory, "source.txt"));
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task Invalid_local_source_prevents_any_upload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "policy-source-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "source.txt"), "not approved");
            var handler = new SourceHandler(SourceText);
            using var http = new HttpClient(handler);
            Assert.Equal("source_integrity_failed", (await Assert.ThrowsAsync<WorkflowException>(() =>
                new BlobPolicySources(http, new SourceCredential(), Settings()).UploadAsync(Catalog(), directory, default))).Code);
            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            File.Delete(Path.Combine(directory, "source.txt"));
            Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData("http://approved.blob.core.windows.net", "policy-documents")]
    [InlineData("https://approved.blob.core.windows.net/path", "policy-documents")]
    [InlineData("https://approved.blob.core.windows.net?sig=secret", "policy-documents")]
    [InlineData("https://approved.blob.core.windows.net", "../escape")]
    [InlineData("https://approved.blob.core.windows.net", "invalid--container")]
    public void Blob_configuration_rejects_unsafe_endpoints_and_containers(string endpoint, string container)
    {
        Assert.Throws<InvalidOperationException>(() => StartupValidation.ValidateBlobSources(
            new() { Mode = "Azure" }, new() { BlobEndpoint = endpoint, BlobContainer = container }));
    }

    [Fact]
    public void Blob_only_commands_do_not_require_model_or_search_settings()
    {
        StartupValidation.ValidateBlobSources(new() { Mode = "Azure" }, Settings());
        Assert.Throws<InvalidOperationException>(() => StartupValidation.ValidateBlobSources(new(), Settings()));
        Assert.Throws<InvalidOperationException>(() => StartupValidation.ValidateIndexer(new() { Mode = "Azure" }, Settings()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Blob_ingestion_verifies_sources_before_any_embedding_or_search_write(bool valid)
    {
        var settings = Settings();
        settings.SearchEndpoint = "https://search.example";
        settings.ApimEndpoint = "https://gateway.example/";
        settings.ApimScope = "api://models/.default";
        settings.EmbeddingModel = "approved-embedding";
        settings.EmbeddingDimensions = 2;
        var handler = new PipelineHandler(valid);
        using var http = new HttpClient(handler);
        var credential = new SourceCredential();
        var sources = new BlobPolicySources(http, credential, settings);
        var indexer = new AzurePolicyIndexer(http, credential, settings,
            new ApimModelClient(http, credential, settings));
        async Task Ingest()
        {
            var approved = await sources.ReadApprovedAsync(Catalog(), default);
            await indexer.IngestAsync(approved, default);
        }
        if (!valid)
        {
            Assert.Equal("source_integrity_failed", (await Assert.ThrowsAsync<WorkflowException>(Ingest)).Code);
            Assert.Equal(1, handler.Calls);
            Assert.Null(handler.UploadBody);
            return;
        }
        await Ingest();
        Assert.Equal(5, handler.Calls);
        using var uploaded = JsonDocument.Parse(handler.UploadBody!);
        var chunks = uploaded.RootElement.GetProperty("value");
        Assert.Equal(2, chunks.GetArrayLength());
        Assert.All(chunks.EnumerateArray(), c =>
            Assert.Equal("restricted-group", c.GetProperty("allowedGroups")[0].GetString()));
        Assert.Equal("UntrustedInstructionFixture", chunks[1].GetProperty("kind").GetString());
        Assert.Equal("Untrusted fictional appendix.", chunks[1].GetProperty("content").GetString());
    }

    private sealed class PipelineHandler(bool valid) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? UploadBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.RequestUri!.Host == "approved.blob.core.windows.net")
                return new(HttpStatusCode.OK) { Content = new StringContent(valid ? SourceText : "tampered") };
            if (request.RequestUri.Host == "gateway.example")
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"embedding":[0.1,0.2]}]}""") };
            if (request.Method == HttpMethod.Put)
                return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
            UploadBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"value":[{"status":true},{"status":true}]}""")
            };
        }
    }

    private sealed class SourceCredential : TokenCredential
    {
        public string? Scope { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scope = Assert.Single(requestContext.Scopes);
            return new("fixture-access-token-not-a-real-credential", DateTimeOffset.UtcNow.AddMinutes(5));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(context, cancellationToken));
    }

    private sealed class SourceHandler(string source, HttpStatusCode getStatus = HttpStatusCode.OK,
        HttpStatusCode putStatus = HttpStatusCode.Created) : HttpMessageHandler
    {
        public string? LastUri { get; private set; }
        public string? Scheme { get; private set; }
        public string? Version { get; private set; }
        public string? Uploaded { get; private set; }
        public bool ConditionalPut { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri!.ToString();
            Scheme = request.Headers.Authorization?.Scheme;
            Version = request.Headers.GetValues("x-ms-version").Single();
            if (request.Method == HttpMethod.Put)
            {
                ConditionalPut = request.Headers.IfNoneMatch.Any(h => h.Tag == "*");
                Uploaded = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new(putStatus);
            }
            return new(getStatus) { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(source))) };
        }
    }

    private sealed class InvalidEncodingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xff, 0xfe]) });
    }
}
