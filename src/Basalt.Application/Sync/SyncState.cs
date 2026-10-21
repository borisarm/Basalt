using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Application.Sync;

/// <summary>One action written to the diagnostic journal (spec 06 §4 and §6, step 6).</summary>
public sealed record SyncJournalAction(string Kind, VaultPath Path, BlobSha? Sha = null)
{
    public static SyncJournalAction From(SyncAction action) => action switch
    {
        SyncAction.Download download => new("download", download.Path, download.RemoteSha),
        SyncAction.Upload upload => new("upload", upload.Path, upload.LocalSha),
        SyncAction.DeleteLocal delete => new("delete_local", delete.Path, delete.ExpectedLocalSha),
        SyncAction.DeleteRemote delete => new("delete_remote", delete.Path),
        SyncAction.MergeText merge => new("merge", merge.Path, merge.RemoteSha),
        SyncAction.CreateConflictCopy copy => new("conflict_copy", copy.Path, copy.Sha),
        SyncAction.NoOp noop => new("noop", noop.Path),
        _ => new("unknown", action.Path),
    };
}

/// <summary>Journal of the cycle in progress: diagnostics only, never required for correctness (spec 06 §4).</summary>
public sealed record SyncJournal(
    int Attempt,
    CommitSha? ExpectedHead,
    Manifest Target,
    IReadOnlyList<SyncJournalAction> Actions,
    DateTimeOffset StartedAt);

/// <summary>How a sync cycle finished (spec 06 §6, step 10).</summary>
public enum SyncOutcome
{
    UpToDate,
    Published,
    Downloaded,
    Merged,
    Failed,
}

/// <summary>One entry of <c>log.jsonl</c> (spec 06 §4); the last 500 are kept.</summary>
public sealed record SyncLogEntry(
    DateTimeOffset When,
    SyncOutcome Outcome,
    CommitSha? Head,
    int Downloaded,
    int Uploaded,
    int DeletedLocal,
    int DeletedRemote,
    int Conflicts,
    int Deferred,
    TimeSpan Duration,
    string? ErrorCode = null);
