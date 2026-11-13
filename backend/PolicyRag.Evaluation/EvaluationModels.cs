using PolicyRag.Application;

namespace PolicyRag.Evaluation;

public sealed record EvaluationCase(
    string Id, string Split, string Role, string Question, string? DocumentId, string Expected,
    string[] RequiredSourceIds, string[] RequiredFacts);

public sealed record CaseResult(
    string CaseId, string Split, string Role, string Question, string? DocumentId,
    string Expected, string Actual, string Outcome, string[] Failures, string Answer,
    string[] CitedSourceIds, string[] RetrievedSourceIds, double DurationMs,
    Evidence Evidence, string? ErrorCode, bool SecurityGatePassed);

public sealed record HumanScore(string CaseId, string Rater, int Relevance, int Completeness, int EvidenceSupport);

public sealed record EvaluationReport(
    string SchemaVersion, string RunId, string Mode, string Variant, string PromptHash,
    string DatasetHash, string CorpusHash, string Revision, bool? DirtyWorktree,
    string Model, DateTimeOffset CreatedAt, string[] Limitations, CaseResult[] Results,
    HumanScore[] HumanScores, string Decision)
{
    public EvaluationSettings? Settings { get; init; }
    public string SettingsHash { get; init; } = "";
    public string ImplementationHash { get; init; } = "";
}

public sealed record EvaluationSettings(
    string EmbeddingModel, int EmbeddingDimensions, string AnswerModel, string GenerationProtocol,
    string SearchIndex, string SearchApiVersion, string Retrieval, int Top, int VectorK,
    int Repetitions, int MaximumCalls);
