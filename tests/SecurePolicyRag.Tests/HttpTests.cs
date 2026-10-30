using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SecurePolicyRag.Tests;

public sealed class HttpTests : IClassFixture<PocFactory>
{
    private readonly HttpClient client;
    public HttpTests(PocFactory factory)
    {
        client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-Profile", "sam");
    }

    [Fact]
    public async Task Ui_and_config_explicitly_label_fixture_mode()
    {
        var html = await client.GetStringAsync("/");
        Assert.Contains("Secure Policy RAG", html);
        Assert.Contains("not real authentication", html);
        var config = await client.GetFromJsonAsync<JsonElement>("/api/config");
        Assert.Equal("Fixture", config.GetProperty("mode").GetString());
        Assert.Contains("No Azure or model calls", config.GetProperty("notice").GetString());
        var response = await client.GetAsync("/health");
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
    }

    [Fact]
    public async Task Denial_has_zero_calls_and_no_restricted_source_content()
    {
        client.DefaultRequestHeaders.Remove("X-Demo-Profile");
        client.DefaultRequestHeaders.Add("X-Demo-Profile", "alex");
        var documents = await client.GetFromJsonAsync<DocumentSummary[]>("/api/documents");
        Assert.DoesNotContain(documents!, d => d.Id == "access-review");
        var response = await client.PostAsJsonAsync("/api/ask", new AskRequest("access review", "access-review"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        var evidence = json.RootElement.GetProperty("evidence");
        Assert.Equal("Denied", evidence.GetProperty("authorization").GetString());
        Assert.Equal(0, evidence.GetProperty("embeddingCalls").GetInt32());
        Assert.Equal(0, evidence.GetProperty("generationCalls").GetInt32());
        Assert.Equal(0, evidence.GetProperty("searchCalls").GetInt32());
        Assert.DoesNotContain("90 days", text);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/documents/access-review")).StatusCode);
    }

    [Fact]
    public async Task Free_text_question_without_document_excludes_restricted_content_for_employee()
    {
        client.DefaultRequestHeaders.Remove("X-Demo-Profile");
        client.DefaultRequestHeaders.Add("X-Demo-Profile", "alex");
        var response = await client.PostAsJsonAsync("/api/ask",
            new AskRequest("What are the privileged access review requirements?"));
        response.EnsureSuccessStatusCode();
        var draft = await response.Content.ReadFromJsonAsync<DraftView>();
        Assert.NotNull(draft);
        Assert.DoesNotContain(draft.Citations, c => c.DocumentId == "access-review");
        Assert.DoesNotContain("90 days", draft.Answer);
    }

    [Fact]
    public async Task Free_text_question_without_document_can_surface_restricted_content_for_reviewer()
    {
        var response = await client.PostAsJsonAsync("/api/ask",
            new AskRequest("What are the privileged access review requirements?"));
        response.EnsureSuccessStatusCode();
        var draft = await response.Content.ReadFromJsonAsync<DraftView>();
        Assert.NotNull(draft);
        Assert.Contains(draft.Citations, c => c.DocumentId == "access-review");
    }

    [Fact]
    public async Task Ask_then_review_is_enforced_by_api()
    {
        var response = await client.PostAsJsonAsync("/api/ask",
            new AskRequest("privileged access review requirements", "access-review"));
        response.EnsureSuccessStatusCode();
        var draft = await response.Content.ReadFromJsonAsync<DraftView>();
        Assert.NotNull(draft);
        Assert.Equal("Pending", draft.ReviewStatus);
        Assert.Contains("90 days", draft.Answer);
        Assert.Equal(0, draft.Evidence.GenerationCalls);
        var accepted = await client.PostAsJsonAsync($"/api/drafts/{draft.Id}/review", new ReviewRequest("accept"));
        accepted.EnsureSuccessStatusCode();
        Assert.Equal("AcceptedForDraftUse",
            (await accepted.Content.ReadFromJsonAsync<DraftView>())!.ReviewStatus);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync($"/api/drafts/{draft.Id}/review", new ReviewRequest("reject"))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Demo-Profile");
        client.DefaultRequestHeaders.Add("X-Demo-Profile", "alex");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/drafts/{draft.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"/api/drafts/{draft.Id}/review", new ReviewRequest("accept"))).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("mallory")]
    public async Task Missing_or_unknown_fixture_identity_is_not_defaulted(string profile)
    {
        client.DefaultRequestHeaders.Remove("X-Demo-Profile");
        if (profile.Length > 0) client.DefaultRequestHeaders.Add("X-Demo-Profile", profile);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/documents")).StatusCode);
    }

    [Fact]
    public async Task Cross_origin_post_is_rejected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ask")
        {
            Content = JsonContent.Create(new AskRequest("access review"))
        };
        request.Headers.Add("Origin", "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Invalid_input_is_visible_and_includes_correlation()
    {
        var response = await client.PostAsJsonAsync("/api/ask", new AskRequest(""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.Headers.Contains("X-Correlation-ID"), response.Headers + "\n" + body);
        Assert.Contains("invalid_question", body);
    }

    [Fact]
    public async Task Malformed_json_is_not_success()
    {
        var response = await client.PostAsync("/api/ask",
            new StringContent("{broken", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"question\":null}")]
    public async Task Missing_question_or_body_is_an_explicit_failure(string body)
    {
        var response = await client.PostAsync("/api/ask",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Correlation-ID"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("code", out _));
        Assert.True(json.RootElement.TryGetProperty("evidence", out _));
    }

    [Fact]
    public async Task Document_controller_returns_only_authorized_source()
    {
        var policy = await client.GetFromJsonAsync<PolicyDocument>("/api/documents/access-review");
        Assert.NotNull(policy);
        Assert.Equal("access-review", policy.Id);
        Assert.Contains(policy.Chunks, chunk => chunk.Text.Contains("90 days", StringComparison.Ordinal));
    }
}

public sealed class PocFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Rag:Mode", "Fixture");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter>(new LoopbackStartupFilter());
        });
    }

    private sealed class LoopbackStartupFilter : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
            Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
