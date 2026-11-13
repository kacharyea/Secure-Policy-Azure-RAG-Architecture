namespace PolicyRag.Application;

public sealed class RagOptions
{
    public string PromptVariant { get; set; } = "baseline";
    public string Mode { get; set; } = "Fixture";
    public bool UseEntraAuthentication { get; set; }
    public bool RequiresEntraAuthentication => Mode == "Azure" || UseEntraAuthentication;
    public int MaximumDrafts { get; set; } = 500;
    public int DraftLifetimeMinutes { get; set; } = 60;
}

public sealed record Caller(string Id, string DisplayName, string[] Groups);
public sealed record PolicyChunk(string Id, string Text, string Kind = "Policy");
public sealed record PolicyDocument(
    string Id, string Title, string Version, string[] AllowedGroups, PolicyChunk[] Chunks);
public sealed record DocumentSummary(string Id, string Title, string Version);
public sealed record Passage(
    string ChunkId, string DocumentId, string Title, string Version, string Text, string Kind);
public sealed record ModelQuote(string ChunkId, string Quote);
public sealed record ModelAnswer(string Status, ModelQuote[] Excerpts);
public sealed record Citation(
    string ChunkId, string DocumentId, string Title, string Version, string Quote);
public sealed record AskRequest(string Question, string? DocumentId = null);
public sealed record ReviewRequest(string Decision);
public sealed record Evidence(
    string Mode, string Authorization, int EmbeddingCalls, int GenerationCalls, int SearchCalls,
    string Validation, string ReviewStatus, string CorrelationId, string[] SourceIds)
{
    public string TraceId { get; init; } = "";
    public string PromptVariant { get; init; } = "baseline";
    public StageEvidence[] Stages { get; init; } = [];
    public TokenUsage Usage { get; init; } = new(null, null, null);
}
public sealed record StageEvidence(string Name, double DurationMs, string Outcome, bool Synthetic);
public sealed record TokenUsage(long? EmbeddingTokens, long? InputTokens, long? OutputTokens);
public sealed record Draft(
    Guid Id, string OwnerId, string Question, string Answer, string[] EvidenceGaps,
    Citation[] Citations, string[] DocumentIds, string Status, string ReviewStatus,
    DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt, Evidence Evidence);
public sealed record DraftView(
    Guid Id, string Answer, string[] EvidenceGaps, Citation[] Citations, string Status,
    string ReviewStatus, DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt, Evidence Evidence)
{
    public static DraftView From(Draft draft) => new(
        draft.Id, draft.Answer, draft.EvidenceGaps, draft.Citations, draft.Status,
        draft.ReviewStatus, draft.CreatedAt, draft.ReviewedAt, draft.Evidence);
}

public sealed class RunMetrics(string correlationId, string mode)
{
    public string PromptVariant { get; set; } = "baseline";
    public string ExperimentId { get; set; } = "";
    public string TraceId { get; internal set; } = "";
    public List<StageEvidence> Stages { get; } = [];
    public long? EmbeddingTokens { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public string[] RetrievedSourceIds { get; set; } = [];
    public string CorrelationId { get; } = correlationId;
    public string Mode { get; } = mode;
    public string Authorization { get; set; } = "Pending";
    public int EmbeddingCalls { get; set; }
    public int GenerationCalls { get; set; }
    public int SearchCalls { get; set; }
    public string Validation { get; set; } = "NotRun";
    public string AnswerStatus { get; set; } = "NotReturned";
    public Evidence Snapshot(string reviewStatus = "NotApplicable", string[]? sourceIds = null) =>
        new(Mode, Authorization, EmbeddingCalls, GenerationCalls, SearchCalls, Validation,
            reviewStatus, CorrelationId, sourceIds ?? [])
        {
            TraceId = TraceId,
            PromptVariant = PromptVariant,
            Stages = Stages.ToArray(),
            Usage = new(EmbeddingTokens, InputTokens, OutputTokens)
        };
}

public sealed class WorkflowException(
    int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
