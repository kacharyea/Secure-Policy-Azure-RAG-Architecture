using PolicyRag.Application;
using PolicyRag.Application.Interfaces;
using PolicyRag.Application.Services;

namespace PolicyRag.Infrastructure.Storage;

public sealed class ReviewStore(RagOptions options, TimeProvider clock) : IReviewStore
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Draft> drafts = [];

    public void Add(Draft draft)
    {
        lock (gate)
        {
            RemoveExpired();
            if (drafts.Count >= options.MaximumDrafts)
                throw new WorkflowException(503, "draft_capacity_reached",
                    "The local draft store is full. Wait for expiry or restart the POC.");
            drafts.Add(draft.Id, draft);
        }
    }

    public Draft Get(Guid id, Caller caller, DocumentAuthorizationService catalog)
    {
        lock (gate)
        {
            RemoveExpired();
            return Require(id, caller, catalog);
        }
    }

    public Draft Review(Guid id, Caller caller, DocumentAuthorizationService catalog, string decision)
    {
        if (decision is not ("accept" or "reject"))
            throw new WorkflowException(400, "invalid_review", "Decision must be accept or reject.");
        lock (gate)
        {
            RemoveExpired();
            var draft = Require(id, caller, catalog);
            if (draft.Status != "answered")
                throw new WorkflowException(409, "no_supported_answer", "There is no supported draft to accept or reject.");
            if (draft.ReviewStatus != "Pending")
                throw new WorkflowException(409, "already_reviewed", "This draft already has a review decision.");
            var status = decision == "accept" ? "AcceptedForDraftUse" : "Rejected";
            var reviewed = draft with
            {
                ReviewStatus = status,
                ReviewedAt = clock.GetUtcNow(),
                Evidence = draft.Evidence with { ReviewStatus = status }
            };
            drafts[id] = reviewed;
            return reviewed;
        }
    }

    private Draft Require(Guid id, Caller caller, DocumentAuthorizationService catalog)
    {
        if (!drafts.TryGetValue(id, out var draft) || draft.OwnerId != caller.Id ||
            draft.DocumentIds.Any(d => !catalog.CanRead(caller, d)))
            throw new WorkflowException(404, "draft_not_found", "The draft is unavailable to this identity.");
        return draft;
    }

    private void RemoveExpired()
    {
        var cutoff = clock.GetUtcNow().AddMinutes(-options.DraftLifetimeMinutes);
        foreach (var id in drafts.Where(p => p.Value.CreatedAt <= cutoff).Select(p => p.Key).ToArray())
            drafts.Remove(id);
    }
}
