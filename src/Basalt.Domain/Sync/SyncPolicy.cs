namespace Basalt.Domain.Sync;

/// <summary>How text conflicts are resolved (spec 06 §1 and §5).</summary>
public enum TextConflictPolicy
{
    AutoMergeOrConflictCopy,
    AutoMergeOrMarkers,
    PreferLocal,
    PreferRemote,
}

/// <summary>How binary conflicts are resolved (spec 06 §1 and §5).</summary>
public enum BinaryConflictPolicy
{
    KeepBoth,
    PreferLocal,
    PreferRemote,
}

/// <summary>What to do when one side deleted and the other modified (spec 06 §5, rows 10 and 11).</summary>
public enum DeleteConflictPolicy
{
    KeepModified,
    PreferDelete,
}

/// <summary>Synchronisation policy of a vault on this device (spec 06 §1).</summary>
public sealed record SyncPolicy(
    TextConflictPolicy TextConflicts = TextConflictPolicy.AutoMergeOrConflictCopy,
    BinaryConflictPolicy BinaryConflicts = BinaryConflictPolicy.KeepBoth,
    DeleteConflictPolicy DeleteVsModify = DeleteConflictPolicy.KeepModified,
    long MaxFileSizeBytes = 50 * 1024 * 1024)
{
    public static SyncPolicy Default { get; } = new();
}

/// <summary>Which side of a conflict a copy comes from.</summary>
public enum ConflictSide
{
    Local,
    Remote,
}
