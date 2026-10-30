using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace SecurePolicyRag.Tests;

public sealed class EntraHttpTests : IDisposable
{
    private const string Tenant = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Audience = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";
    private const string BrowserClient = "11111111-2222-3333-4444-555555555555";
    private const string EmployeeGroup = "cccccccc-dddd-eeee-ffff-000000000001";
    private const string ReviewerGroup = "cccccccc-dddd-eeee-ffff-000000000002";
    private const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
    private static readonly SymmetricSecurityKey SigningKey = new(RandomNumberGenerator.GetBytes(32));
    private readonly PocFactory fixture = new();
    private readonly WebApplicationFactory<Program> server;
    private readonly HttpClient client;

    public EntraHttpTests()
    {
        server = fixture.WithWebHostBuilder(builder =>
        {
            foreach (var setting in new Dictionary<string, string>
            {
                ["Rag:Mode"] = "Fixture",
                ["Rag:UseEntraAuthentication"] = "true",
                ["Rag:Azure:TenantId"] = Tenant,
                ["Rag:Azure:Audience"] = Audience,
                [$"Rag:Azure:GroupMappings:{EmployeeGroup}:0"] = "employees",
                [$"Rag:Azure:GroupMappings:{ReviewerGroup}:0"] = "security-reviewers",
                ["BrowserSignIn:Enabled"] = "true",
                ["BrowserSignIn:ClientId"] = BrowserClient,
                ["BrowserSignIn:Scope"] = $"api://{Tenant}/policy-rag-api-sample/PolicyReview.Invoke"
            })
                builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureTestServices(services =>
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
                {
                    // The signer/discovery replacement exists only in this test host, never in production configuration.
                    jwt.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        new() { Issuer = Issuer });
                    jwt.TokenValidationParameters.ValidIssuer = Issuer;
                    jwt.TokenValidationParameters.ValidAudience = Audience;
                    jwt.TokenValidationParameters.IssuerSigningKey = SigningKey;
                }));
        });
        client = server.CreateClient();
    }

    [Fact]
    public async Task Identity_profile_labels_fixture_data_and_publishes_only_public_signin_configuration()
    {
        var config = await client.GetFromJsonAsync<JsonElement>("/api/config");
        Assert.Equal("Fixture", config.GetProperty("mode").GetString());
        Assert.Equal("Entra", config.GetProperty("authenticationMode").GetString());
        Assert.Empty(config.GetProperty("profiles").EnumerateArray());
        Assert.Contains("not live RAG", config.GetProperty("notice").GetString());
        Assert.Equal(BrowserClient, config.GetProperty("signIn").GetProperty("clientId").GetString());
        Assert.DoesNotContain("secret", config.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth-callback.html")).StatusCode);
        var asset = await client.GetByteArrayAsync("/vendor/msal/lib/msal-browser.min.js");
        Assert.Equal("K1gBZQZKak7AQevUSDIpkXJoiiOkLDuB1lMUeZ26BOs=", Convert.ToBase64String(SHA256.HashData(asset)));
    }

    [Fact]
    public async Task Synthetic_headers_cannot_authenticate_identity_profile()
    {
        client.DefaultRequestHeaders.Add("X-Demo-Profile", "sam");
        client.DefaultRequestHeaders.Add("X-User-Groups", "security-reviewers");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/documents")).StatusCode);
    }

    [Fact]
    public async Task Employee_token_lists_only_authorized_documents_and_restricted_request_has_zero_calls()
    {
        Authenticate([EmployeeGroup]);
        client.DefaultRequestHeaders.Add("X-Demo-Profile", "sam");
        var documents = await client.GetFromJsonAsync<DocumentSummary[]>("/api/documents");
        Assert.NotEmpty(documents!);
        Assert.DoesNotContain(documents!, document => document.Id == "access-review");
        var denied = await client.PostAsJsonAsync("/api/ask", new AskRequest("access review", "access-review"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var body = await denied.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var evidence = json.RootElement.GetProperty("evidence");
        Assert.Equal(0, evidence.GetProperty("generationCalls").GetInt32());
        Assert.Equal(0, evidence.GetProperty("embeddingCalls").GetInt32());
        Assert.Equal(0, evidence.GetProperty("searchCalls").GetInt32());
        Assert.DoesNotContain("90 days", body);
    }

    [Fact]
    public async Task Reviewer_token_allows_restricted_document_but_review_is_still_scoped_to_user_oid()
    {
        Authenticate([EmployeeGroup, ReviewerGroup]);
        var response = await client.PostAsJsonAsync("/api/ask", new AskRequest("access review requirements", "access-review"));
        response.EnsureSuccessStatusCode();
        var draft = (await response.Content.ReadFromJsonAsync<DraftView>())!;
        Assert.Contains("90 days", draft.Answer);
        Authenticate([EmployeeGroup, ReviewerGroup], "another-test-user");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/drafts/{draft.Id}")).StatusCode);
    }

    [Theory]
    [InlineData("oid", "identity_unresolved")]
    [InlineData("scope", "scope_required")]
    [InlineData("groups", "no_entitlements")]
    [InlineData("overage", "no_entitlements")]
    public async Task Valid_signature_does_not_replace_required_identity_scope_and_entitlements(string missing, string code)
    {
        var claims = new List<Claim>();
        if (missing != "oid") claims.Add(new("oid", "test-user"));
        if (missing != "scope") claims.Add(new("scp", "PolicyReview.Invoke"));
        if (missing is not ("groups" or "overage")) claims.Add(new("groups", EmployeeGroup));
        if (missing == "overage") claims.Add(new("hasgroups", "true"));
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(claims));
        var response = await client.GetAsync("/api/documents");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(code, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("signature")]
    [InlineData("expired")]
    public async Task Invalid_tokens_are_rejected_without_falling_back_to_fixture_personas(string invalid)
    {
        client.DefaultRequestHeaders.Add("X-Demo-Profile", "sam");
        var key = invalid == "signature" ? new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) : SigningKey;
        var token = Token([new("oid", "test-user"), new("scp", "PolicyReview.Invoke"), new("groups", ReviewerGroup)],
            invalid == "audience" ? BrowserClient : Audience,
            invalid == "issuer" ? "https://untrusted.example" : Issuer,
            key, invalid == "expired" ? DateTime.UtcNow.AddMinutes(-2) : null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.GetAsync("/api/documents");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync());
    }

    private void Authenticate(string[] groups, string oid = "test-user") =>
        client.DefaultRequestHeaders.Authorization = new("Bearer",
            Token([new("oid", oid), new("scp", "PolicyReview.Invoke"), .. groups.Select(group => new Claim("groups", group))]));

    private static string Token(IEnumerable<Claim> claims, string audience = Audience, string issuer = Issuer,
        SecurityKey? key = null, DateTime? expires = null) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience, claims,
            DateTime.UtcNow.AddMinutes(-10), expires ?? DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.HmacSha256)));

    public void Dispose()
    {
        client.Dispose();
        server.Dispose();
        fixture.Dispose();
    }
}
