using Basalt.Application.Ports.Remote;
using Basalt.Application.Sync;
using Basalt.Domain.Accounts;

namespace Basalt.Adapters.GitHub;

/// <summary>Creates authenticated repositories for a configured vault (spec 06 §10).</summary>
internal sealed class GitHubRemoteRepositoryFactory(GitHubApiClientFactory clients) : IRemoteRepositoryFactory
{
    public IRemoteRepository Create(GitHubAccountId account, RemoteRepositoryRef repository) =>
        new GitHubRemoteRepository(clients.Create(account, repository), repository);
}
