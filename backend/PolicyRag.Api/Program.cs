using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using PolicyRag.Api;
using PolicyRag.Application;
using PolicyRag.Application.Services;
using PolicyRag.Infrastructure;
using PolicyRag.Infrastructure.Configuration;
using PolicyRag.Infrastructure.Storage;

if (args.Contains("--ingest", StringComparer.Ordinal))
    throw new InvalidOperationException("Ingestion moved to PolicyRag.Indexer. Run that project with --ingest.");
var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection("Rag").Get<RagOptions>() ?? new();
var azure = builder.Configuration.GetSection("Rag:Azure").Get<AzureOptions>() ?? new();
StartupValidation.Validate(options, builder.Environment.IsDevelopment(), azure);
var browser = builder.Configuration.GetSection("BrowserSignIn").Get<BrowserSignInOptions>() ?? new();
browser.Validate(options, builder.Environment);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.AddPolicyRagInfrastructure(options, azure, builder.Environment.IsDevelopment());
builder.Services.AddSingleton(_ => JsonPolicyManifest.Load(
    Path.Combine(AppContext.BaseDirectory, "Data", "policies.json")));
builder.Services.AddScoped<RagOrchestrationService>();
builder.Services.AddSingleton<AnswerValidationService>();
builder.Services.AddScoped<CallerContextFilter>();
builder.Services.AddControllers(mvc => mvc.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)
    .ConfigureApiBehaviorOptions(behavior => behavior.InvalidModelStateResponseFactory = _ =>
        throw new WorkflowException(400, "invalid_request", "The request body is invalid."));
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(rate => rate.AddFixedWindowLimiter("api", limit =>
{
    limit.PermitLimit = 60;
    limit.Window = TimeSpan.FromMinutes(1);
    limit.QueueLimit = 0;
    limit.AutoReplenishment = true;
}).RejectionStatusCode = StatusCodes.Status429TooManyRequests);

if (options.RequiresEntraAuthentication)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
    {
        jwt.Authority = $"https://login.microsoftonline.com/{azure.TenantId}/v2.0";
        jwt.Audience = azure.Audience;
        jwt.MapInboundClaims = false;
        jwt.IncludeErrorDetails = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
    builder.Services.AddAuthorization();
}

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.TraceIdentifier = Guid.NewGuid().ToString("N");
    context.Items["metrics"] = new RunMetrics(context.TraceIdentifier, options.Mode);
    context.Response.OnStarting(() =>
    {
        ApplyResponseHeaders(context, browser.Enabled);
        return Task.CompletedTask;
    });
    await next();
});
app.UseExceptionHandler(error => error.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var metrics = context.Items["metrics"] as RunMetrics;
    var status = exception switch
    {
        WorkflowException workflow => workflow.StatusCode,
        BadHttpRequestException bad => bad.StatusCode,
        JsonException => 400,
        OperationCanceledException => 504,
        AuthenticationFailedException or CredentialUnavailableException => 502,
        HttpRequestException => 502,
        _ => 500
    };
    var code = exception is WorkflowException failure ? failure.Code : status switch
    {
        400 => "invalid_request",
        504 => "upstream_timeout",
        502 => "upstream_unavailable",
        _ => "request_failed"
    };
    var message = exception is WorkflowException known ? known.Message : status switch
    {
        400 => "The request body is invalid.",
        502 => "A dependency or its authentication failed. No fixture fallback was used.",
        504 => "The request timed out. No draft was approved.",
        _ => "The request failed. Use the correlation identifier when investigating."
    };
    // Record decisions, never raw questions, tokens, source text, or upstream error bodies.
    app.Logger.LogWarning("Request failed: {Code}, HTTP {Status}, correlation {CorrelationId}",
        code, status, metrics?.CorrelationId ?? context.TraceIdentifier);
    ApplyResponseHeaders(context, browser.Enabled);
    context.Response.StatusCode = status;
    context.Response.ContentType = "application/problem+json";
    await context.Response.WriteAsJsonAsync(new
    {
        type = "about:blank",
        title = message,
        status,
        code,
        correlationId = metrics?.CorrelationId ?? context.TraceIdentifier,
        evidence = metrics?.Snapshot()
    });
}));

app.Use(async (context, next) =>
{
    if (options.Mode == "Fixture" && (context.Connection.RemoteIpAddress is not { } address ||
                                     !IPAddress.IsLoopback(address)))
        throw new WorkflowException(403, "local_only", "Fixture mode accepts only loopback connections.");
    if (context.Request.Path.StartsWithSegments("/api") &&
        !HttpMethods.IsGet(context.Request.Method) &&
        context.Request.Headers.Origin is { Count: > 0 } origins &&
        (!Uri.TryCreate(origins.ToString(), UriKind.Absolute, out var origin) ||
         origin.Authority != context.Request.Host.Value || origin.Scheme != context.Request.Scheme))
        throw new WorkflowException(403, "origin_rejected", "Cross-origin mutation requests are not allowed.");
    await next();
});
if (options.RequiresEntraAuthentication)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "ready", mode = options.Mode }));
app.MapGet("/api/config", () => new
{
    mode = options.Mode,
    authenticationMode = options.RequiresEntraAuthentication ? "Entra" : "Synthetic",
    notice = options.Mode == "Fixture"
        ? options.RequiresEntraAuthentication
            ? "IDENTITY DEMO: real Entra tokens and group authorization; FIXTURE data, keyword retrieval, " +
              "scripted quotations. No Search or model calls. This is not live RAG."
            : "LOCAL FIXTURE: synthetic identities, keyword retrieval, scripted quotations. No Azure or model calls."
        : "AZURE: live dependencies; a delegated API bearer token is required. No fixture fallback.",
    profiles = !options.RequiresEntraAuthentication ? CallerResolver.DemoCallers.Select(p => new { p.Id, p.DisplayName }) : [],
    signIn = browser.Enabled ? new
    {
        browser.ClientId,
        authority = $"https://login.microsoftonline.com/{azure.TenantId}",
        browser.Scope,
        browser.RedirectUri
    } : null,
    answerModel = options.Mode == "Azure" ? azure.AnswerModel : "scripted-extractive-fixture",
    embeddingModel = options.Mode == "Azure" ? azure.EmbeddingModel : "deterministic-fixture-not-semantic"
});

var api = app.MapControllers().RequireRateLimiting("api");
if (options.RequiresEntraAuthentication) api.RequireAuthorization();
app.Run();

static void ApplyResponseHeaders(HttpContext context, bool browserSignIn)
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; " +
        (browserSignIn
            ? "connect-src 'self' https://login.microsoftonline.com; frame-src https://login.microsoftonline.com 'self'; "
            : "connect-src 'self'; ") +
        "img-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
}

public partial class Program;
