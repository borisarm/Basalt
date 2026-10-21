using Basalt.Application.Ports.Remote;
using Basalt.Application.Sync;
using Basalt.Domain.Accounts;

namespace Basalt.Testing.Fakes;

/// <summary>Returns an in-memory repository per ref and remembers the created instances.</summary>
public sealed class InMemoryRemoteRepositoryFactory : IRemoteRepositoryFactory
{
    private readonly Dictionary<string, InMemoryRemoteRepository> _repositories = new(StringComparer.Ordinal);

    public IReadOnlyCollection<InMemoryRemoteRepository> All => _repositories.Values;

    public InMemoryRemoteRepository? Get(string owner, string name, string branch) =>
        _repositories.GetValueOrDefault($"{owner}/{name}@{branch}");

    public IRemoteRepository Create(GitHubAccountId account, RemoteRepositoryRef repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var key = $"{repository.Owner}/{repository.Name}@{repository.Branch}";
        return _repositories.TryGetValue(key, out var existing)
            ? existing
            : _repositories[key] = new InMemoryRemoteRepository(repository);
    }
}
