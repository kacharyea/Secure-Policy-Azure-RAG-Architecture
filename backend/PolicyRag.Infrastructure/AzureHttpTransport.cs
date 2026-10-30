using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;
using Azure.Core;
using PolicyRag.Application;

namespace PolicyRag.Infrastructure;

internal sealed class AzureHttpTransport(HttpClient http, TokenCredential credential)
{
    public async Task AuthenticateAsync(
        HttpRequestMessage request, string scope, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken, HttpStatusCode? acceptedStatus = null)
    {
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != acceptedStatus)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new WorkflowException(502, "upstream_rejected",
                $"An upstream service rejected the request (HTTP {status}). No fixture fallback was used.");
        }
        return response;
    }

    public static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, cancellationToken);
            return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        }
        catch (JsonException)
        {
            throw new WorkflowException(502, "invalid_upstream_json", "An upstream service returned invalid JSON.");
        }
    }

    public static string Required(JsonElement element, string property) =>
        element.GetProperty(property).GetString() ?? throw new FormatException("Missing string field.");
}
