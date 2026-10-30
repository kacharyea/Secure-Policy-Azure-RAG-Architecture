using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using PolicyRag.Application;
using PolicyRag.Application.Interfaces;
using PolicyRag.Infrastructure.AI;
using PolicyRag.Infrastructure.Configuration;
using PolicyRag.Infrastructure.Search;
using PolicyRag.Infrastructure.Storage;

namespace PolicyRag.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPolicyRagInfrastructure(
        this IServiceCollection services, RagOptions options, AzureOptions azure, bool development)
    {
        services.AddSingleton(options);
        services.AddSingleton(azure);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IReviewStore, ReviewStore>();
        if (options.Mode == "Azure")
        {
            services.AddSingleton<TokenCredential>(_ => development
                ? new DefaultAzureCredential()
                : string.IsNullOrWhiteSpace(azure.ManagedIdentityClientId)
                    ? new ManagedIdentityCredential()
                    : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(
                        azure.ManagedIdentityClientId)));
            services.AddHttpClient<ApimModelClient>(http => http.Timeout = TimeSpan.FromSeconds(30))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            services.AddHttpClient<AzurePolicySearch>(http => http.Timeout = TimeSpan.FromSeconds(30))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            services.AddTransient<IEmbeddingService>(sp => sp.GetRequiredService<ApimModelClient>());
            services.AddTransient<IAnswerGenerator>(sp => sp.GetRequiredService<ApimModelClient>());
            services.AddTransient<IPolicySearch>(sp => sp.GetRequiredService<AzurePolicySearch>());
        }
        else
        {
            services.AddSingleton<FixtureProvider>();
            services.AddSingleton<IEmbeddingService>(sp => sp.GetRequiredService<FixtureProvider>());
            services.AddSingleton<IAnswerGenerator>(sp => sp.GetRequiredService<FixtureProvider>());
            services.AddSingleton<IPolicySearch>(sp => sp.GetRequiredService<FixtureProvider>());
        }
        return services;
    }
}
