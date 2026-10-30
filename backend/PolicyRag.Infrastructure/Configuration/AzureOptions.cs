namespace PolicyRag.Infrastructure.Configuration;

public sealed class AzureOptions
{
    public string TenantId { get; set; } = "";
    public string Audience { get; set; } = "";
    public string SearchEndpoint { get; set; } = "";
    public string SearchIndex { get; set; } = "policy-chunks";
    public string SearchApiVersion { get; set; } = "2024-07-01";
    public string ApimEndpoint { get; set; } = "";
    public string ApimScope { get; set; } = "";
    public string EmbeddingPath { get; set; } = "models/embeddings";
    public string GenerationPath { get; set; } = "models/chat/completions";
    public string EmbeddingModel { get; set; } = "";
    public string AnswerModel { get; set; } = "";
    public int EmbeddingDimensions { get; set; } = 1536;
    public string ManagedIdentityClientId { get; set; } = "";
    public string GenerationProtocol { get; set; } = "OpenAI";
    public string BlobEndpoint { get; set; } = "";
    public string BlobContainer { get; set; } = "policy-documents";
}
