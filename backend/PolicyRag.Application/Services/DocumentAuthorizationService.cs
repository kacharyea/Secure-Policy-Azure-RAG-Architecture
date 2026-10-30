using System.Text.RegularExpressions;

namespace PolicyRag.Application.Services;

public sealed partial class DocumentAuthorizationService
{
    private readonly Dictionary<string, PolicyDocument> documents;

    public DocumentAuthorizationService(IEnumerable<PolicyDocument> policies)
    {
        documents = policies.ToDictionary(p => p.Id, StringComparer.Ordinal);
        if (documents.Count == 0 ||
            documents.Values.Any(p => !SafeId().IsMatch(p.Id) ||
                p.AllowedGroups.Length == 0 || p.AllowedGroups.Any(g => !SafeId().IsMatch(g)) ||
                string.IsNullOrWhiteSpace(p.Title) || string.IsNullOrWhiteSpace(p.Version) ||
                p.Chunks.Length == 0 || p.Chunks.Any(c =>
                    !SafeId().IsMatch(c.Id) || string.IsNullOrWhiteSpace(c.Text))) ||
            documents.Values.SelectMany(p => p.Chunks).Select(c => c.Id).Distinct().Count() !=
            documents.Values.Sum(p => p.Chunks.Length))
        {
            throw new InvalidOperationException("The policy manifest contains invalid or duplicate metadata.");
        }
    }

    public bool CanRead(Caller caller, string documentId) =>
        documents.TryGetValue(documentId, out var document) &&
        document.AllowedGroups.Intersect(caller.Groups, StringComparer.Ordinal).Any();

    public IReadOnlyList<PolicyDocument> ApprovedDocuments => documents.Values.ToArray();

    public PolicyDocument RequireDocument(Caller caller, string documentId)
    {
        if (!CanRead(caller, documentId))
            throw new WorkflowException(403, "document_access_denied",
                "The requested document is unavailable to this identity.");
        return documents[documentId];
    }

    public IReadOnlyList<PolicyDocument> Authorized(Caller caller) =>
        documents.Values.Where(p => CanRead(caller, p.Id)).ToArray();

    public void ValidatePassages(Caller caller, IReadOnlyList<Passage> passages)
    {
        foreach (var passage in passages)
        {
            var document = RequireDocument(caller, passage.DocumentId);
            var chunk = document.Chunks.SingleOrDefault(c => c.Id == passage.ChunkId);
            if (chunk is null || chunk.Text != passage.Text || document.Version != passage.Version ||
                document.Title != passage.Title || chunk.Kind != passage.Kind)
            {
                throw new WorkflowException(502, "source_integrity_failed",
                    "Retrieved content does not match the approved policy manifest.");
            }
        }
    }

    public static Passage ToPassage(PolicyDocument document, PolicyChunk chunk) =>
        new(chunk.Id, document.Id, document.Title, document.Version, chunk.Text, chunk.Kind);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,100}$")]
    private static partial Regex SafeId();
}
