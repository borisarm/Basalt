using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Application.Ports.Remote;

/// <summary>Identifies one branch of a repository on a GitHub host (spec 03 §1.4).</summary>
public sealed record RemoteRepositoryRef(GitHubHost Host, string Owner, string Name, string Branch);

/// <summary>A branch head and the tree it points to.</summary>
public sealed record RemoteHead(CommitSha Commit, TreeSha Tree);

/// <summary>A single change applied on top of a base tree (spec 03 §1.4).</summary>
public abstract record TreeChange(VaultPath Path)
{
    public sealed record Upsert(VaultPath Path, BlobSha Sha) : TreeChange(Path);

    public sealed record Delete(VaultPath Path) : TreeChange(Path);
}

/// <summary>Author and committer of a commit; the instant comes from the adapter clock.</summary>
public sealed record CommitAuthor(string Name, string Email);

/// <summary>One entry of the remote file history (spec 06 §13).</summary>
public sealed record RemoteCommitInfo(
    CommitSha Sha,
    string Message,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset When);

/// <summary>Repository metadata used while configuring sync (spec 06 §10).</summary>
public sealed record RemoteRepositoryInfo(
    string Owner,
    string Name,
    string DefaultBranch,
    bool IsPrivate,
    bool IsArchived,
    bool CanPush)
{
    public string FullName => Owner + "/" + Name;
}

/// <summary>Error codes of the remote port (spec 06 §9.2 and §13).</summary>
public static class RemoteErrors
{
    public const string Unauthorized = "remote.unauthorized";
    public const string Forbidden = "remote.forbidden";
    public const string NotFound = "remote.not_found";
    public const string RateLimited = "remote.rate_limited";
    public const string NonFastForward = "remote.non_fast_forward";
    public const string Conflict = "remote.conflict";
    public const string ValidationFailed = "remote.validation_failed";
    public const string ServerError = "remote.server_error";
    public const string Network = "remote.network";
    public const string Timeout = "remote.timeout";
    public const string RepositoryEmpty = "remote.repository_empty";
}

/// <summary>Read/write operations against one remote branch (spec 03 §1.4).</summary>
public interface IRemoteRepository
{
    /// <summary>Returns null when the branch does not exist or the repository is empty.</summary>
    Task<Result<RemoteHead?>> GetHeadAsync(CancellationToken ct);

    /// <summary>Reads the whole tree recursively and flattens it into a manifest.</summary>
    Task<Result<Manifest>> GetTreeAsync(TreeSha tree, CancellationToken ct);

    Task<Result<byte[]>> GetBlobAsync(BlobSha sha, CancellationToken ct);

    Task<Result<BlobSha>> CreateBlobAsync(ReadOnlyMemory<byte> content, CancellationToken ct);

    Task<Result<TreeSha>> CreateTreeAsync(TreeSha? baseTree, IReadOnlyList<TreeChange> changes, CancellationToken ct);

    Task<Result<CommitSha>> CreateCommitAsync(
        string message,
        TreeSha tree,
        IReadOnlyList<CommitSha> parents,
        CommitAuthor author,
        CancellationToken ct);

    /// <summary>Fast-forward only; returns <c>remote.non_fast_forward</c> otherwise.</summary>
    Task<Result<Unit>> UpdateBranchAsync(CommitSha newHead, CommitSha? expectedOld, CancellationToken ct);

    Task<Result<IReadOnlyList<RemoteCommitInfo>>> GetFileHistoryAsync(VaultPath path, int max, CancellationToken ct);

    Task<Result<byte[]?>> GetFileAtCommitAsync(VaultPath path, CommitSha commit, CancellationToken ct);
}

/// <summary>Repository-level operations used while configuring sync (spec 03 §1.4, 06 §10).</summary>
public interface IRepositoryAdministration
{
    Task<Result<IReadOnlyList<RemoteRepositoryInfo>>> ListAccessibleRepositoriesAsync(
        GitHubAccountId account,
        CancellationToken ct);

    Task<Result<RemoteRepositoryInfo>> CreateRepositoryAsync(
        GitHubAccountId account,
        string? org,
        string name,
        bool isPrivate,
        CancellationToken ct);

    Task<Result<RemoteRepositoryInfo>> GetRepositoryAsync(
        GitHubAccountId account,
        string owner,
        string name,
        CancellationToken ct);

    /// <summary>Creates a branch at the current head of the default branch.</summary>
    Task<Result<Unit>> CreateBranchAsync(
        GitHubAccountId account,
        string owner,
        string name,
        string branch,
        CancellationToken ct);
}
