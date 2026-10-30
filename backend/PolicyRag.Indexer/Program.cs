using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PolicyRag.Application;
using PolicyRag.Infrastructure;
using PolicyRag.Infrastructure.Configuration;
using PolicyRag.Infrastructure.Search;
using PolicyRag.Infrastructure.Storage;

if (args is ["--help"])
{
    Console.WriteLine("PolicyRag.Indexer commands (exactly one required):");
    Console.WriteLine("  --upload-sources : upload approved Data/SourceDocuments/*.txt files to private Blob storage.");
    Console.WriteLine("  --verify-sources : read all approved Blob sources and verify integrity; no Search/model calls.");
    Console.WriteLine("  --ingest-blob    : verify all Blob sources, then embed and index using approved metadata/chunk boundaries.");
    Console.WriteLine("  --ingest         : existing local-manifest ingestion without Blob access.");
    Console.WriteLine("Configure Rag__Mode=Azure and Rag__Azure__* environment variables. DOTNET_ENVIRONMENT=Development uses developer credentials.");
    return 0;
}
if (args.Length != 1 || args[0] is not ("--ingest" or "--ingest-blob" or "--upload-sources" or "--verify-sources"))
{
    Console.Error.WriteLine("No cloud operation performed. Run PolicyRag.Indexer with one explicit command; see --help.");
    return 1;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory
});
var options = builder.Configuration.GetSection("Rag").Get<RagOptions>() ?? new();
var azure = builder.Configuration.GetSection("Rag:Azure").Get<AzureOptions>() ?? new();
try
{
    if (args[0] != "--ingest")
        StartupValidation.ValidateBlobSources(options, azure);
    if (args[0] is "--ingest" or "--ingest-blob")
        StartupValidation.ValidateIndexer(options, azure);
}
catch (InvalidOperationException failure)
{
    Console.Error.WriteLine(failure.Message);
    return 1;
}
builder.Services.AddPolicyRagInfrastructure(options, azure, builder.Environment.IsDevelopment());
builder.Services.AddHttpClient<AzurePolicyIndexer>(http => http.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<BlobPolicySources>(http => http.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PolicyRag.Indexer");
try
{
    var catalog = JsonPolicyManifest.Load(Path.Combine(AppContext.BaseDirectory, "Data", "policies.json"));
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    if (args[0] == "--upload-sources")
    {
        var (uploaded, unchanged) = await host.Services.GetRequiredService<BlobPolicySources>()
            .UploadAsync(catalog, Path.Combine(AppContext.BaseDirectory, "Data", "SourceDocuments"), timeout.Token);
        logger.LogInformation("Approved Blob sources: {Uploaded} uploaded, {Unchanged} unchanged and verified.", uploaded, unchanged);
        return 0;
    }
    if (args[0] is "--verify-sources" or "--ingest-blob")
    {
        catalog = await host.Services.GetRequiredService<BlobPolicySources>().ReadApprovedAsync(catalog, timeout.Token);
        logger.LogInformation("All {Count} Blob sources match the approved manifest.", catalog.ApprovedDocuments.Count);
        if (args[0] == "--verify-sources") return 0;
    }
    await host.Services.GetRequiredService<AzurePolicyIndexer>().IngestAsync(catalog, timeout.Token);
    logger.LogInformation("Approved fictional policies were indexed successfully using {Source}.",
        args[0] == "--ingest-blob" ? "verified Blob sources" : "the local manifest");
    return 0;
}
catch (Exception failure) when (failure is WorkflowException or HttpRequestException or
                              AuthenticationFailedException or CredentialUnavailableException or
                              OperationCanceledException or IOException)
{
    // Do not log upstream bodies, policy contents, credentials, or raw exception messages.
    logger.LogError("Ingestion failed: {Code}. No fixture fallback was used.",
        failure is WorkflowException workflow ? workflow.Code : failure.GetType().Name);
    return 1;
}
