namespace Basalt.Application.Sync;

/// <summary>Error codes of the sync engine (spec 06 §6 and §9.4).</summary>
public static class SyncErrors
{
    public const string ShaMismatch = "sync.sha_mismatch";
    public const string LocalFileMissing = "sync.local_file_missing";
    public const string TreeMismatch = "sync.tree_mismatch";
}
