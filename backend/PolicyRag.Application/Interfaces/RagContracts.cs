using PolicyRag.Application.Services;

namespace PolicyRag.Application.Interfaces;

public interface IEmbeddingService
{
    Task<float[]> EmbedAsync(string text, RunMetrics metrics, CancellationToken cancellationToken);
}

public interface IPolicySearch
{
    Task<IReadOnlyList<Passage>> SearchAsync(
        Caller caller, string question, string? documentId, float[] vector,
        RunMetrics metrics, CancellationToken cancellationToken);
}

public interface IAnswerGenerator
{
    Task<ModelAnswer> GenerateAsync(
        string question, IReadOnlyList<Passage> passages, RunMetrics metrics,
        CancellationToken cancellationToken);
}

public interface IReviewStore
{
    void Add(Draft draft);
    Draft Get(Guid id, Caller caller, DocumentAuthorizationService authorization);
    Draft Review(Guid id, Caller caller, DocumentAuthorizationService authorization, string decision);
}
