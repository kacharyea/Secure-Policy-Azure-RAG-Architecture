using System.Text.RegularExpressions;
using PolicyRag.Application;

namespace PolicyRag.Infrastructure.Configuration;

public static partial class StartupValidation
{
    public static void Validate(RagOptions options, bool development, AzureOptions? azure = null)
    {
        _ = AI.PolicyPrompts.Get(options.PromptVariant);
        if (options.Mode is not ("Fixture" or "Azure"))
            throw new InvalidOperationException("Rag:Mode must be Fixture or Azure.");
        if (options.MaximumDrafts is < 1 or > 10000 || options.DraftLifetimeMinutes is < 1 or > 1440)
            throw new InvalidOperationException("Invalid bounded draft-store settings.");
        if (options.Mode == "Fixture")
        {
            if (!development)
                throw new InvalidOperationException("Fixture mode is allowed only in Development on loopback.");
            if (options.RequiresEntraAuthentication)
                ValidateIdentity(azure ?? new AzureOptions());
            return;
        }
        ValidateAzure(azure ?? new AzureOptions(), runtime: true);
    }

    public static void ValidateIndexer(RagOptions options, AzureOptions azure)
    {
        if (options.Mode != "Azure")
            throw new InvalidOperationException("The indexer requires explicitly configured Rag:Mode=Azure.");
        ValidateAzure(azure, runtime: false);
    }

    public static void ValidateBlobSources(RagOptions options, AzureOptions azure)
    {
        if (options.Mode != "Azure" || !HttpsEndpoint(azure.BlobEndpoint) ||
            new Uri(azure.BlobEndpoint).AbsolutePath != "/" ||
            azure.BlobContainer.Length is < 3 or > 63 ||
            !ContainerName().IsMatch(azure.BlobContainer))
            throw new InvalidOperationException(
                "Blob source commands require Rag:Mode=Azure, an HTTPS account-root BlobEndpoint, " +
                "and a valid private BlobContainer. See README.");
    }

    private static void ValidateAzure(AzureOptions a, bool runtime)
    {
        if (!HttpsEndpoint(a.SearchEndpoint) || !HttpsEndpoint(a.ApimEndpoint) ||
            !IndexName().IsMatch(a.SearchIndex) || a.SearchApiVersion != "2024-07-01" ||
            string.IsNullOrWhiteSpace(a.ApimScope) || !a.ApimScope.EndsWith("/.default", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(a.EmbeddingModel) || !SafeRelativePath(a.EmbeddingPath) ||
            a.EmbeddingDimensions is < 1 or > 4096)
            throw new InvalidOperationException(
                "Azure access requires HTTPS endpoints, an embedding alias, APIM scope/path, " +
                "a valid search index, and embedding dimensions. See README.");
        if (!runtime) return;
        ValidateIdentity(a);
        if (string.IsNullOrWhiteSpace(a.AnswerModel) || !SafeRelativePath(a.GenerationPath) ||
            a.GenerationProtocol is not ("OpenAI" or "Anthropic"))
            throw new InvalidOperationException(
                "Azure mode requires valid tenant/audience, HTTPS endpoints, model aliases, APIM scope/paths, " +
                "a valid search index, supported protocol, and embedding dimensions. See README.");
    }

    private static void ValidateIdentity(AzureOptions a)
    {
        if (!Guid.TryParse(a.TenantId, out _) || string.IsNullOrWhiteSpace(a.Audience))
            throw new InvalidOperationException("Entra authentication requires a valid tenant and API audience.");
    }

    private static bool HttpsEndpoint(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static bool SafeRelativePath(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.StartsWith('/') &&
        !value.Contains("..", StringComparison.Ordinal) && !value.Contains('\\') &&
        Uri.TryCreate(value, UriKind.Relative, out _);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,126}[a-z0-9]$")]
    private static partial Regex IndexName();

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex ContainerName();
}
