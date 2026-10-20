using Basalt.Application.Ports.Remote;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;

namespace Basalt.Testing.Fakes;

/// <summary>In-memory repository administration used by application and sync tests (spec 03 §1.4).</summary>
public sealed class InMemoryRepositoryAdministration : IRepositoryAdministration
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FakeRemoteRepository> _repositories = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryRepositoryAdministration(params RemoteRepositoryInfo[] repositories)
    {
        foreach (var repository in repositories)
        {
            _repositories[Key(repository.Owner, repository.Name)] = new FakeRemoteRepository(repository);
        }
    }

    public Task<Result<IReadOnlyList<RemoteRepositoryInfo>>> ListAccessibleRepositoriesAsync(
        GitHubAccountId account,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(Result<IReadOnlyList<RemoteRepositoryInfo>>.Success(
                _repositories.Values.Select(repository => repository.Info).ToList()));
        }
    }

    public Task<Result<RemoteRepositoryInfo>> CreateRepositoryAsync(
        GitHubAccountId account,
        string? org,
        string name,
        bool isPrivate,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var owner = org ?? "basalt-test";
        lock (_gate)
        {
            var key = Key(owner, name);
            if (_repositories.ContainsKey(key))
            {
                return Task.FromResult(Result<RemoteRepositoryInfo>.Failure(
                    new Error(RemoteErrors.ValidationFailed, "Repository already exists.")));
            }

            var info = new RemoteRepositoryInfo(owner, name, "main", isPrivate, false, true);
            _repositories[key] = new FakeRemoteRepository(info);
            return Task.FromResult(Result<RemoteRepositoryInfo>.Success(info));
        }
    }

    public Task<Result<RemoteRepositoryInfo>> GetRepositoryAsync(
        GitHubAccountId account,
        string owner,
        string name,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_repositories.TryGetValue(Key(owner, name), out var repository)
                ? Result<RemoteRepositoryInfo>.Success(repository.Info)
                : Result<RemoteRepositoryInfo>.Failure(
                    new Error(RemoteErrors.NotFound, $"Repository {owner}/{name} does not exist.")));
        }
    }

    public Task<Result<Unit>> CreateBranchAsync(
        GitHubAccountId account,
        string owner,
        string name,
        string branch,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_repositories.TryGetValue(Key(owner, name), out var repository))
            {
                return Task.FromResult(Result<Unit>.Failure(
                    new Error(RemoteErrors.NotFound, $"Repository {owner}/{name} does not exist.")));
            }

            if (!repository.Branches.Add(branch))
            {
                return Task.FromResult(Result<Unit>.Failure(
                    new Error(RemoteErrors.ValidationFailed, "Branch already exists.")));
            }

            return Task.FromResult(Result<Unit>.Success(Unit.Value));
        }
    }

    private static string Key(string owner, string name) => owner + "/" + name;

    private sealed class FakeRemoteRepository(RemoteRepositoryInfo info)
    {
        public RemoteRepositoryInfo Info { get; } = info;

        public HashSet<string> Branches { get; } = new(StringComparer.Ordinal) { info.DefaultBranch };
    }
}
