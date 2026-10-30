using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PolicyRag.Application;
using PolicyRag.Application.Interfaces;
using PolicyRag.Application.Services;

namespace PolicyRag.Infrastructure.AI;

public sealed partial class FixtureProvider(DocumentAuthorizationService catalog)
    : IEmbeddingService, IPolicySearch, IAnswerGenerator
{
    public Task<float[]> EmbedAsync(string text, RunMetrics metrics, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A deterministic fixture vector, not a semantic embedding or a cloud model call.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Task.FromResult(hash.Take(8).Select(b => b / 255f).ToArray());
    }

    public Task<IReadOnlyList<Passage>> SearchAsync(
        Caller caller, string question, string? documentId, float[] vector,
        RunMetrics metrics, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        metrics.SearchCalls++;
        var terms = Words().Matches(question.ToLowerInvariant()).Select(m => m.Value)
            .Where(w => w.Length > 2 && !StopWords.Contains(w)).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<Passage> results = catalog.Authorized(caller)
            .Where(p => documentId is null || p.Id == documentId)
            .SelectMany(p => p.Chunks.Select(c => DocumentAuthorizationService.ToPassage(p, c)))
            .Select(p => new
            {
                Passage = p,
                Score = terms.Count(t =>
                Words().Matches((p.Text + " " + p.Title).ToLowerInvariant()).Any(m => m.Value == t))
            })
            .Where(p => p.Score > 0)
            .OrderByDescending(p => p.Score).ThenBy(p => p.Passage.ChunkId, StringComparer.Ordinal)
            .Take(5).Select(p => p.Passage).ToArray();
        return Task.FromResult(results);
    }

    public Task<ModelAnswer> GenerateAsync(
        string question, IReadOnlyList<Passage> passages, RunMetrics metrics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Selection is scripted. It cannot demonstrate a real model resisting prompt injection.
        var excerpts = passages.Where(p => p.Kind == "Policy").Take(3)
            .Select(p => new ModelQuote(p.ChunkId, p.Text)).ToArray();
        return Task.FromResult(new ModelAnswer(
            excerpts.Length == 0 ? "insufficient_evidence" : "answered", excerpts));
    }

    private static readonly HashSet<string> StopWords =
        ["the", "and", "what", "are", "does", "this", "that", "for", "with", "from",
         "please", "tell", "about", "how", "policy", "have", "can", "show", "requirements"];

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex Words();
}
