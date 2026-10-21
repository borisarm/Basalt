using Basalt.Application.Auth;
using Basalt.Application.Ports.Auth;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Security;
using Basalt.Application.Sync;
using Basalt.Domain.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Basalt.Adapters.GitHub;

/// <summary>Registers the GitHub adapter (spec 03 §1.4).</summary>
public static class GitHubAdapterServiceCollectionExtensions
{
    public static IServiceCollection AddBasaltGitHubAdapter(this IServiceCollection services, GitHubHost host)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(host);
        services.AddHttpClient("github-oauth");
        services.TryAddSingleton<IGitHubAuthenticator>(provider => new GitHubDeviceFlowAuthenticator(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("github-oauth"), "basalt"));
        services.TryAddSingleton<IGitHubTokenProvider>(provider => new GitHubTokenProvider(
            provider.GetRequiredService<IGitHubAuthenticator>(),
            provider.GetRequiredService<ISecretStore>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<GitHubApiClientFactory>();
        services.TryAddSingleton<IRemoteRepositoryFactory>(provider => new GitHubRemoteRepositoryFactory(provider.GetRequiredService<GitHubApiClientFactory>()));
        services.TryAddSingleton<IRepositoryAdministration>(provider =>
            new GitHubRepositoryAdministration(provider.GetRequiredService<GitHubApiClientFactory>(), host));
        return services;
    }
}
