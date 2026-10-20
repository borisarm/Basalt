using Basalt.Application.Auth;
using Basalt.Application.Ports.Remote;
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
        services.TryAddSingleton<IGitHubTokenProvider, UnavailableGitHubTokenProvider>();
        services.TryAddSingleton<GitHubApiClientFactory>();
        services.TryAddSingleton<IRepositoryAdministration>(provider =>
            new GitHubRepositoryAdministration(provider.GetRequiredService<GitHubApiClientFactory>(), host));
        return services;
    }
}
