namespace PolicyRag.Application.Services;

public sealed class AnswerValidationService
{
    public Citation[] Validate(ModelAnswer model, IReadOnlyList<Passage> passages, RunMetrics metrics)
    {
        if (model.Status is not ("answered" or "insufficient_evidence") ||
            model.Excerpts is null || model.Excerpts.Length > 5 ||
            (model.Status == "answered" && model.Excerpts.Length == 0) ||
            (model.Status == "insufficient_evidence" && model.Excerpts.Length != 0))
            return Invalid();

        var citations = new List<Citation>();
        foreach (var excerpt in model.Excerpts)
        {
            if (excerpt is null)
                return Invalid();
            var passage = passages.SingleOrDefault(p => p.ChunkId == excerpt.ChunkId);
            if (passage is null || passage.Kind != "Policy" ||
                string.IsNullOrWhiteSpace(excerpt.Quote) || excerpt.Quote.Length < 15 ||
                !passage.Text.Contains(excerpt.Quote, StringComparison.Ordinal) ||
                citations.Any(c => c.ChunkId == excerpt.ChunkId && c.Quote == excerpt.Quote))
                return Invalid();
            citations.Add(new(passage.ChunkId, passage.DocumentId, passage.Title, passage.Version, excerpt.Quote));
        }
        metrics.Validation = "AuthorizedExactQuotes";
        return citations.ToArray();

        Citation[] Invalid()
        {
            metrics.Validation = "Failed";
            throw new WorkflowException(502, "output_validation_failed",
                "The generated output failed source or response-contract validation. No usable draft was created.");
        }
    }
}
