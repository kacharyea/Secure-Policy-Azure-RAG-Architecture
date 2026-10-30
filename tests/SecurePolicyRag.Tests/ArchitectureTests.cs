using Microsoft.Extensions.DependencyInjection;
using PolicyRag.Api.Controllers;

namespace SecurePolicyRag.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Application_has_no_infrastructure_or_host_dependencies()
    {
        var references = typeof(RagOrchestrationService).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, r =>
            r.Name!.StartsWith("Azure", StringComparison.Ordinal) ||
            r.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
            r.Name.StartsWith("PolicyRag.Infrastructure", StringComparison.Ordinal) ||
            r.Name.StartsWith("PolicyRag.Api", StringComparison.Ordinal));
        Assert.Equal(typeof(RagOrchestrationService).Assembly, typeof(IReviewStore).Assembly);
        Assert.NotEqual(typeof(RagOrchestrationService).Assembly, typeof(ReviewStore).Assembly);
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_the_web_host()
    {
        Assert.DoesNotContain(typeof(ApimModelClient).Assembly.GetReferencedAssemblies(),
            r => r.Name == typeof(QuestionsController).Assembly.GetName().Name ||
                 r.Name!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Api_does_not_register_an_ingestion_writer()
    {
        using var factory = new PocFactory();
        Assert.Null(factory.Services.GetService<AzurePolicyIndexer>());
        Assert.Null(factory.Services.GetService<BlobPolicySources>());
        Assert.IsType<FixtureProvider>(factory.Services.GetRequiredService<IPolicySearch>());
        Assert.IsType<ReviewStore>(factory.Services.GetRequiredService<IReviewStore>());
    }
}
