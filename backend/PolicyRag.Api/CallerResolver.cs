using System.Security.Claims;
using PolicyRag.Application;

namespace PolicyRag.Api;

public static class CallerResolver
{
    public static readonly Caller[] DemoCallers =
    [
        new("alex", "Alex - employee (synthetic)", ["employees"]),
        new("sam", "Sam - security reviewer (synthetic)", ["employees", "security-reviewers"])
    ];

    public static Caller Resolve(HttpContext context, RagOptions options, IConfiguration configuration)
    {
        if (!options.RequiresEntraAuthentication)
        {
            var profile = context.Request.Headers["X-Demo-Profile"].ToString();
            return DemoCallers.SingleOrDefault(p => p.Id == profile)
                ?? throw new WorkflowException(401, "demo_identity_required",
                    "Choose a known synthetic profile using X-Demo-Profile: alex or sam.");
        }
        if (context.User.Identity?.IsAuthenticated != true)
            throw new WorkflowException(401, "authentication_required", "A valid API access token is required.");
        var id = context.User.FindFirstValue("oid");
        if (string.IsNullOrWhiteSpace(id))
            throw new WorkflowException(403, "identity_unresolved", "A trusted user identity could not be resolved.");
        var scopes = context.User.FindFirstValue("scp")?.Split(' ') ?? [];
        if (!scopes.Contains("PolicyReview.Invoke", StringComparer.Ordinal))
            throw new WorkflowException(403, "scope_required", "The delegated PolicyReview.Invoke scope is required.");
        var mapping = configuration.GetSection("Rag:Azure:GroupMappings").GetChildren()
            .ToDictionary(c => c.Key, c => c.Get<string[]>() ?? [], StringComparer.Ordinal);
        var groups = context.User.FindAll("groups").Select(g => g.Value)
            .Where(mapping.ContainsKey).SelectMany(g => mapping[g])
            .Where(g => g is "employees" or "security-reviewers")
            .Distinct(StringComparer.Ordinal).ToArray();
        if (groups.Length == 0)
            throw new WorkflowException(403, "no_entitlements",
                "No document entitlements were resolved. Group-overage tokens require an explicit resolver.");
        return new(id, "Authenticated employee", groups);
    }
}
