using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using PolicyRag.Application;
using PolicyRag.Application.Interfaces;
using PolicyRag.Infrastructure.Configuration;
using static PolicyRag.Infrastructure.AzureHttpTransport;

namespace PolicyRag.Infrastructure.Search;

public sealed class AzurePolicySearch(
    HttpClient http, TokenCredential credential, AzureOptions options) : IPolicySearch
{
    private readonly AzureHttpTransport transport = new(http, credential);

    public async Task<IReadOnlyList<Passage>> SearchAsync(
        Caller caller, string question, string? documentId, float[] vector,
        RunMetrics metrics, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AzureSearchRequest.Uri(options, "docs/search"))
        {
            Content = JsonContent.Create(new
            {
                search = question,
                filter = AzureSearchRequest.Filter(caller, documentId),
                vectorFilterMode = "preFilter",
                vectorQueries = new[] { new { kind = "vector", vector, fields = "vector", k = 20 } },
                select = "id,documentId,title,version,content,kind",
                top = 5
            })
        };
        await transport.AuthenticateAsync(request, "https://search.azure.com/.default", cancellationToken);
        metrics.SearchCalls++;
        using var response = await transport.SendAsync(request, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        try
        {
            var values = json.RootElement.GetProperty("value").EnumerateArray().ToArray();
            if (values.Length > 5)
                throw new FormatException("Too many passages.");
            return values.Select(v => new Passage(
                Required(v, "id"), Required(v, "documentId"), Required(v, "title"),
                Required(v, "version"), Required(v, "content"), Required(v, "kind"))).ToArray();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new WorkflowException(502, "invalid_search_response", "The search response is invalid.");
        }
    }
}
