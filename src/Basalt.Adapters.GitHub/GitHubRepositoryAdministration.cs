using Basalt.Application.Ports.Remote;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Domain.Sync;

namespace Basalt.Adapters.GitHub;

/// <summary>Lists, creates and inspects repositories and creates branches (spec 03 §1.4, 06 §10).</summary>
internal sealed class GitHubRepositoryAdministration : IRepositoryAdministration
{
    private const int MaxPages = 100;

    private readonly GitHubApiClientFactory _clients;
    private readonly GitHubHost _host;

    public GitHubRepositoryAdministration(GitHubApiClientFactory clients, GitHubHost host)
    {
        _clients = clients;
        _host = host;
    }

    public async Task<Result<IReadOnlyList<RemoteRepositoryInfo>>> ListAccessibleRepositoriesAsync(
        GitHubAccountId account,
        CancellationToken ct)
    {
        var client = AdminClient(account);
        var repositories = new List<RemoteRepositoryInfo>();
        string? next = null;
        for (var page = 1; page <= MaxPages; page++)
        {
            var result = await client.ListRepositoriesAsync(page, 100, ct).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return Result<IReadOnlyList<RemoteRepositoryInfo>>.Failure(result.Error);
            }

            repositories.AddRange(result.Value.Items.Select(Map));
            next = result.Value.Next;
            if (next is null)
            {
                break;
            }
        }

        return Result<IReadOnlyList<RemoteRepositoryInfo>>.Success(repositories);
    }

    public async Task<Result<RemoteRepositoryInfo>> CreateRepositoryAsync(
        GitHubAccountId account,
        string? org,
        string name,
        bool isPrivate,
        CancellationToken ct)
    {
        var result = await AdminClient(account).CreateRepositoryAsync(org, name, isPrivate, ct).ConfigureAwait(false);
        return result.IsSuccess ? Map(result.Value) : Result<RemoteRepositoryInfo>.Failure(result.Error);
    }

    public async Task<Result<RemoteRepositoryInfo>> GetRepositoryAsync(
        GitHubAccountId account,
        string owner,
        string name,
        CancellationToken ct)
    {
        var result = await AdminClient(account).GetRepositoryAsync(owner, name, ct).ConfigureAwait(false);
        return result.IsSuccess ? Map(result.Value) : Result<RemoteRepositoryInfo>.Failure(result.Error);
    }

    public async Task<Result<Unit>> CreateBranchAsync(
        GitHubAccountId account,
        string owner,
        string name,
        string branch,
        CancellationToken ct)
    {
        var info = await GetRepositoryAsync(account, owner, name, ct).ConfigureAwait(false);
        if (info.IsFailure)
        {
            return Result<Unit>.Failure(info.Error);
        }

        var repository = _clients.Create(
            account, new RemoteRepositoryRef(_host, owner, name, info.Value.DefaultBranch));
        var head = await repository.GetRefAsync(null, ct).ConfigureAwait(false);
        if (head.IsFailure)
        {
            return Result<Unit>.Failure(head.Error);
        }

        var created = await repository.CreateRefAsync(branch, CommitSha.Parse(head.Value.Value!.Object.Sha).Value, ct)
            .ConfigureAwait(false);
        return created.IsSuccess ? Result<Unit>.Success(Unit.Value) : Result<Unit>.Failure(created.Error);
    }

    private GitHubApiClient AdminClient(GitHubAccountId account) =>
        _clients.Create(account, new RemoteRepositoryRef(_host, string.Empty, string.Empty, "main"));

    private static RemoteRepositoryInfo Map(GitHubRepositoryDto repository)
    {
        var owner = repository.Owner?.Login
            ?? repository.FullName.Split('/', 2)[0];
        return new RemoteRepositoryInfo(
            owner,
            repository.Name,
            repository.DefaultBranch,
            repository.Private,
            repository.Archived,
            repository.Permissions?.Push ?? false);
    }
}
