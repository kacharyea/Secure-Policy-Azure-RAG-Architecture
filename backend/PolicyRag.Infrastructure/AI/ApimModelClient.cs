using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using PolicyRag.Application;
using PolicyRag.Application.Interfaces;
using PolicyRag.Infrastructure.Configuration;
using static PolicyRag.Infrastructure.AzureHttpTransport;

namespace PolicyRag.Infrastructure.AI;

public sealed class ApimModelClient(
    HttpClient http, TokenCredential credential, AzureOptions options) : IEmbeddingService, IAnswerGenerator
{
    private readonly AzureHttpTransport transport = new(http, credential);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<float[]> EmbedAsync(
        string text, RunMetrics metrics, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApimUri(options.EmbeddingPath))
        {
            Content = JsonContent.Create(new
            {
                model = options.EmbeddingModel,
                input = text,
                dimensions = options.EmbeddingDimensions
            })
        };
        await transport.AuthenticateAsync(request, options.ApimScope, cancellationToken);
        metrics.EmbeddingCalls++;
        using var response = await transport.SendAsync(request, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        metrics.EmbeddingTokens = Usage(json.RootElement, "total_tokens");
        try
        {
            var vector = json.RootElement.GetProperty("data")[0].GetProperty("embedding")
                .EnumerateArray().Select(v => v.GetSingle()).ToArray();
            if (vector.Length != options.EmbeddingDimensions || vector.Any(v => !float.IsFinite(v)) ||
                vector.All(v => v == 0))
                throw new FormatException("Invalid embedding dimensions or values.");
            return vector;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or
                                   IndexOutOfRangeException or FormatException or OverflowException)
        {
            throw new WorkflowException(502, "invalid_embedding", "The embedding response is invalid.");
        }
    }

    public async Task<ModelAnswer> GenerateAsync(
        string question, IReadOnlyList<Passage> passages, RunMetrics metrics,
        CancellationToken cancellationToken)
    {
        var instructions = PolicyPrompts.Get(metrics.PromptVariant);
        var context = JsonSerializer.Serialize(new { question, passages }, JsonOptions);
        object body = options.GenerationProtocol == "Anthropic"
            ? new
            {
                model = options.AnswerModel,
                max_tokens = 1400,
                system = instructions,
                messages = new[] { new { role = "user", content = context } }
            }
            : new
            {
                model = options.AnswerModel,
                max_completion_tokens = 1400,
                messages = new[]
                {
                    new { role = "system", content = instructions },
                    new { role = "user", content = context }
                }
            };
        using var request = new HttpRequestMessage(HttpMethod.Post, ApimUri(options.GenerationPath))
        {
            Content = JsonContent.Create(body)
        };
        if (options.GenerationProtocol == "Anthropic")
            request.Headers.Add("anthropic-version", "2023-06-01");
        await transport.AuthenticateAsync(request, options.ApimScope, cancellationToken);
        metrics.GenerationCalls++;
        using var response = await transport.SendAsync(request, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        metrics.InputTokens = Usage(json.RootElement,
            options.GenerationProtocol == "Anthropic" ? "input_tokens" : "prompt_tokens");
        metrics.OutputTokens = Usage(json.RootElement,
            options.GenerationProtocol == "Anthropic" ? "output_tokens" : "completion_tokens");
        try
        {
            var content = options.GenerationProtocol == "Anthropic"
                ? string.Concat(json.RootElement.GetProperty("content").EnumerateArray()
                    .Where(c => Required(c, "type") == "text").Select(c => Required(c, "text")))
                : Required(json.RootElement.GetProperty("choices")[0].GetProperty("message"), "content");
            var result = JsonSerializer.Deserialize<ModelAnswer>(content, JsonOptions);
            return result ?? throw new JsonException("Missing model output.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or
                                   InvalidOperationException or IndexOutOfRangeException or FormatException)
        {
            throw new WorkflowException(502, "invalid_generation_response",
                "The model did not return the required JSON contract.");
        }
    }

    private static long? Usage(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("usage", out var usage) &&
        usage.ValueKind == JsonValueKind.Object &&
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var count) && count >= 0
            ? count : null;

    private Uri ApimUri(string path) => new(new Uri(options.ApimEndpoint.TrimEnd('/') + "/"), path);
}
