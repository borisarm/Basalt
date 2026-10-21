using Basalt.Application.Sync;

namespace Basalt.Adapters.Platform.Json;

internal sealed record ManifestEntryDto(string Path, string Sha, long Size);

internal sealed record SyncBaseDto(
    string? LastSyncedCommit,
    string? LastRemoteTree,
    IReadOnlyList<ManifestEntryDto> Manifest);

internal sealed record HashCacheEntryDto(string Path, long Size, long ModifiedUtcTicks, string Sha);

internal sealed record HashCacheDto(IReadOnlyList<HashCacheEntryDto> Entries);

internal sealed record JournalActionDto(string Kind, string Path, string? Sha);

internal sealed record SyncJournalDto(
    int Attempt,
    string? ExpectedHead,
    IReadOnlyList<ManifestEntryDto> Target,
    IReadOnlyList<JournalActionDto> Actions,
    DateTimeOffset StartedAt);

internal sealed record RemotePathIssueDto(string Path, string Reason);

internal sealed record RemoteTreeDto(
    string Tree,
    IReadOnlyList<ManifestEntryDto> Files,
    IReadOnlyList<RemotePathIssueDto> Quarantined,
    long SavedAtUtcTicks);

internal sealed record SyncLogEntryDto(
    DateTimeOffset When,
    SyncOutcome Outcome,
    string? Head,
    int Downloaded,
    int Uploaded,
    int DeletedLocal,
    int DeletedRemote,
    int Conflicts,
    int Deferred,
    long DurationMilliseconds,
    string? ErrorCode);
