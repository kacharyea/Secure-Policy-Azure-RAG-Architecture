using PolicyRag.Application.Interfaces;

namespace PolicyRag.Application.Services;

public sealed class RagOrchestrationService(
    DocumentAuthorizationService catalog, IEmbeddingService embeddings, IPolicySearch search,
    IAnswerGenerator generator, AnswerValidationService validation, IReviewStore store, TimeProvider clock)
{
    public async Task<Draft> AskAsync(
        Caller caller, AskRequest request, RunMetrics metrics, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 2000 ||
            request.DocumentId?.Length > 100)
            throw new WorkflowException(400, "invalid_question",
                "Provide a question of 1-2000 characters and an optional document identifier of at most 100 characters.");

        if (request.DocumentId is not null && !catalog.CanRead(caller, request.DocumentId))
        {
            metrics.Authorization = "Denied";
            throw new WorkflowException(403, "document_access_denied",
                "The requested document is unavailable to this identity.");
        }
        if (catalog.Authorized(caller).Count == 0)
        {
            metrics.Authorization = "Denied";
            throw new WorkflowException(403, "no_entitlements", "No policy access was granted to this identity.");
        }
        metrics.Authorization = "Allowed";
        var vector = await embeddings.EmbedAsync(request.Question, metrics, cancellationToken);
        var passages = await search.SearchAsync(
            caller, request.Question, request.DocumentId, vector, metrics, cancellationToken);
        catalog.ValidatePassages(caller, passages);
        if (passages.Select(p => p.ChunkId).Distinct(StringComparer.Ordinal).Count() != passages.Count ||
            (request.DocumentId is not null && passages.Any(p => p.DocumentId != request.DocumentId)))
            throw new WorkflowException(502, "retrieval_scope_failed",
                "The retrieved passages did not satisfy the requested scope.");

        var model = passages.Count == 0
            ? new ModelAnswer("insufficient_evidence", [])
            : await generator.GenerateAsync(request.Question, passages, metrics, cancellationToken);
        var citations = validation.Validate(model, passages, metrics);
        var answered = model.Status == "answered";
        var reviewStatus = answered ? "Pending" : "NotApplicable";
        var draft = new Draft(
            Guid.NewGuid(), caller.Id, request.Question,
            answered ? string.Join("\n\n", citations.Select(c => c.Quote)) :
                "No supported answer was found in the authorized retrieved policy passages.",
            ["Policy statements are not proof that a control was performed. Verify execution evidence independently.",
             "This is an extractive policy draft, not a compliance determination."],
            citations, passages.Select(p => p.DocumentId).Distinct(StringComparer.Ordinal).ToArray(),
            model.Status, reviewStatus, clock.GetUtcNow(), null,
            metrics.Snapshot(reviewStatus, citations.Select(c => c.ChunkId).ToArray()));
        store.Add(draft);
        return draft;
    }

}
