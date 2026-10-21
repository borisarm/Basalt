using Basalt.Application.Ports.Remote;
using Basalt.Domain.Accounts;

namespace Basalt.Application.Sync;

/// <summary>Creates the remote repository bound to an account and repository ref (spec 06 §10).</summary>
public interface IRemoteRepositoryFactory
{
    IRemoteRepository Create(GitHubAccountId account, RemoteRepositoryRef repository);
}
