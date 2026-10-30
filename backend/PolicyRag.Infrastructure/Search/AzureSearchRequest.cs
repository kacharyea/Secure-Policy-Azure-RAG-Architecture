using PolicyRag.Application;
using PolicyRag.Infrastructure.Configuration;

namespace PolicyRag.Infrastructure.Search;

public static class AzureSearchRequest
{
    public static Uri Uri(AzureOptions options, string path) =>
        new($"{options.SearchEndpoint.TrimEnd('/')}/indexes/{options.SearchIndex}" +
            (path.Length == 0 ? "" : "/" + path) + $"?api-version={options.SearchApiVersion}");

    public static string Filter(Caller caller, string? documentId)
    {
        if (caller.Groups.Length == 0)
            throw new WorkflowException(403, "no_entitlements", "No document entitlements were resolved.");
        static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
        var groups = string.Join(" or ", caller.Groups.Distinct(StringComparer.Ordinal)
            .Select(g => $"allowedGroups/any(g: g eq '{Escape(g)}')"));
        return documentId is null
            ? $"({groups})"
            : $"({groups}) and documentId eq '{Escape(documentId)}'";
    }
}
