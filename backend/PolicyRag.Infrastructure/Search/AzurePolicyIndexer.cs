using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using PolicyRag.Application;
using PolicyRag.Application.Interfaces;
using PolicyRag.Application.Services;
using PolicyRag.Infrastructure.Configuration;
using static PolicyRag.Infrastructure.AzureHttpTransport;

namespace PolicyRag.Infrastructure.Search;

public sealed class AzurePolicyIndexer(
    HttpClient http, TokenCredential credential, AzureOptions options, IEmbeddingService embeddings)
{
    private readonly AzureHttpTransport transport = new(http, credential);

    public async Task IngestAsync(DocumentAuthorizationService catalog, CancellationToken cancellationToken)
    {
        var fields = new object[]
        {
            new { name = "id", type = "Edm.String", key = true, filterable = true },
            new { name = "documentId", type = "Edm.String", filterable = true },
            new { name = "title", type = "Edm.String", searchable = true },
            new { name = "version", type = "Edm.String", filterable = true },
            new { name = "content", type = "Edm.String", searchable = true },
            new { name = "kind", type = "Edm.String", filterable = true },
            new { name = "allowedGroups", type = "Collection(Edm.String)", filterable = true },
            new { name = "vector", type = "Collection(Edm.Single)", searchable = true,
                dimensions = options.EmbeddingDimensions, vectorSearchProfile = "policy-profile" }
        };
        using var create = new HttpRequestMessage(HttpMethod.Put, AzureSearchRequest.Uri(options, ""))
        {
            Content = JsonContent.Create(new
            {
                name = options.SearchIndex,
                fields,
                vectorSearch = new
                {
                    algorithms = new[] { new { name = "policy-hnsw", kind = "hnsw" } },
                    profiles = new[] { new { name = "policy-profile", algorithm = "policy-hnsw" } }
                }
            })
        };
        await transport.AuthenticateAsync(create, "https://search.azure.com/.default", cancellationToken);
        using var indexResponse = await transport.SendAsync(create, cancellationToken);
        var metrics = new RunMetrics(Guid.NewGuid().ToString("N"), "Azure");
        var documents = new List<object>();
        foreach (var policy in catalog.ApprovedDocuments)
        {
            foreach (var chunk in policy.Chunks)
            {
                documents.Add(new Dictionary<string, object>
                {
                    ["@search.action"] = "upload",
                    ["id"] = chunk.Id,
                    ["documentId"] = policy.Id,
                    ["title"] = policy.Title,
                    ["version"] = policy.Version,
                    ["content"] = chunk.Text,
                    ["kind"] = chunk.Kind,
                    ["allowedGroups"] = policy.AllowedGroups,
                    ["vector"] = await embeddings.EmbedAsync(chunk.Text, metrics, cancellationToken)
                });
            }
        }
        using var upload = new HttpRequestMessage(HttpMethod.Post, AzureSearchRequest.Uri(options, "docs/index"))
        {
            Content = JsonContent.Create(new { value = documents })
        };
        await transport.AuthenticateAsync(upload, "https://search.azure.com/.default", cancellationToken);
        using var uploadResponse = await transport.SendAsync(upload, cancellationToken);
        using var result = await ReadJsonAsync(uploadResponse, cancellationToken);
        if (!result.RootElement.TryGetProperty("value", out var statuses) ||
            statuses.GetArrayLength() != documents.Count ||
            statuses.EnumerateArray().Any(v => !v.TryGetProperty("status", out var ok) ||
                                               ok.ValueKind != JsonValueKind.True))
            throw new WorkflowException(502, "indexing_failed", "Search did not confirm every indexed chunk.");
    }
}
