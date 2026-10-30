using PolicyRag.Application;

namespace PolicyRag.Api;

public sealed class BrowserSignInOptions
{
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = "";
    public string Scope { get; set; } = "";
    public string RedirectUri { get; set; } = "http://localhost:5280/auth-callback.html";

    public void Validate(RagOptions options, IWebHostEnvironment environment)
    {
        if (!Enabled) return;
        if (!options.RequiresEntraAuthentication || !Guid.TryParse(ClientId, out _) ||
            !Uri.TryCreate(Scope, UriKind.Absolute, out var scope) || scope.Scheme != "api" ||
            !scope.AbsolutePath.EndsWith("/PolicyReview.Invoke", StringComparison.Ordinal) ||
            !Uri.TryCreate(RedirectUri, UriKind.Absolute, out var redirect) ||
            !string.IsNullOrEmpty(redirect.UserInfo) || !string.IsNullOrEmpty(redirect.Query) ||
            !string.IsNullOrEmpty(redirect.Fragment) ||
            !(redirect.Scheme == "https" ||
              (environment.IsDevelopment() && redirect.Scheme == "http" && redirect.IsLoopback)))
            throw new InvalidOperationException("Browser sign-in requires Entra authentication, a client GUID, " +
                "the API delegated scope, and an HTTPS redirect (loopback HTTP only in Development).");
        if (!File.Exists(Path.Combine(environment.ContentRootPath, "wwwroot", "vendor", "msal",
                "lib", "msal-browser.min.js")))
            throw new InvalidOperationException("Restore the pinned browser assets with infra\\Restore-BrowserAssets.ps1.");
    }
}
