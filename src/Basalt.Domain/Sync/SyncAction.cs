using Basalt.Domain.Notes;

namespace Basalt.Domain.Sync;

/// <summary>One step of a synchronisation plan (spec 02 §11 and 06 §5).</summary>
public abstract record SyncAction(VaultPath Path)
{
    public sealed record Download(VaultPath Path, BlobSha RemoteSha) : SyncAction(Path);

    public sealed record Upload(VaultPath Path, BlobSha LocalSha) : SyncAction(Path);

    public sealed record DeleteLocal(VaultPath Path, BlobSha ExpectedLocalSha) : SyncAction(Path);

    public sealed record DeleteRemote(VaultPath Path) : SyncAction(Path);

    public sealed record MergeText(VaultPath Path, BlobSha? BaseSha, BlobSha LocalSha, BlobSha RemoteSha)
        : SyncAction(Path);

    public sealed record CreateConflictCopy(VaultPath Path, ConflictSide Side, BlobSha Sha) : SyncAction(Path);

    public sealed record NoOp(VaultPath Path) : SyncAction(Path);
}

/// <summary>
/// Result of planning: actions in deterministic order plus the manifest expected after applying them. Merge results
/// and conflict-copy names are only known when materialising, so those paths keep the remote SHA in the expected base
/// (ADR-0017).
/// </summary>
public sealed record SyncPlan(IReadOnlyList<SyncAction> Actions, Manifest ExpectedNewBase)
{
    public bool IsEmpty => Actions.Count == 0;
}
