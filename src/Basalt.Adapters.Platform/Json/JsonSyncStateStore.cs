using System.Text;
using System.Text.Json;
using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Sync;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;

namespace Basalt.Adapters.Platform.Json;

/// <summary>
/// Per-vault JSON state in <c>{AppData}/vaults/{id}/sync/</c> (spec 06 §4): atomic base/hash cache/journal, one file per
/// cached remote tree (last three) and an append-only <c>log.jsonl</c> capped at 500 entries.
/// </summary>
internal sealed class JsonSyncStateStore : ISyncStateStore, IDisposable
{
    private const int MaxLogEntries = 500;
    private const int MaxRemoteTrees = 3;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;
    private readonly string _logPath;
    private readonly string _treesDirectory;
    private readonly JsonFileStore<SyncBaseDto> _base;
    private readonly JsonFileStore<HashCacheDto> _cache;
    private readonly JsonFileStore<SyncJournalDto> _journal;
    private readonly SemaphoreSlim _logGate = new(1, 1);
    private readonly TimeProvider _time;
    private int _logLines;

    public JsonSyncStateStore(string syncDirectory, TimeProvider time)
    {
        _time = time;
        _directory = syncDirectory;
        _logPath = Path.Combine(syncDirectory, "log.jsonl");
        _treesDirectory = Path.Combine(syncDirectory, "remote-trees");
        Directory.CreateDirectory(syncDirectory);
        _logLines = File.Exists(_logPath) ? File.ReadAllLines(_logPath).Length : 0;
        _base = new JsonFileStore<SyncBaseDto>(
            Path.Combine(syncDirectory, "base.json"),
            PlatformJsonContext.Default.SyncBaseDto,
            () => new SyncBaseDto(null, null, []),
            currentVersion: 1,
            migrations: [],
            indented: false,
            time);
        _cache = new JsonFileStore<HashCacheDto>(
            Path.Combine(syncDirectory, "hashcache.json"),
            PlatformJsonContext.Default.HashCacheDto,
            () => new HashCacheDto([]),
            currentVersion: 1,
            migrations: [],
            indented: false,
            time);
        _journal = new JsonFileStore<SyncJournalDto>(
            Path.Combine(syncDirectory, "journal.json"),
            PlatformJsonContext.Default.SyncJournalDto,
            () => new SyncJournalDto(0, null, [], [], DateTimeOffset.UnixEpoch),
            currentVersion: 1,
            migrations: [],
            indented: false,
            time);
    }

    public async Task<SyncBase> LoadBaseAsync(CancellationToken ct)
    {
        var load = await _base.LoadAsync(ct).ConfigureAwait(false);
        try
        {
            return new SyncBase(
                load.Value.LastSyncedCommit is { } commit ? CommitSha.Parse(commit).Value : null,
                load.Value.LastRemoteTree is { } tree ? TreeSha.Parse(tree).Value : null,
                ToManifest(load.Value.Manifest));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return SyncBase.Empty;
        }
    }

    public async Task<Result<Unit>> SaveBaseAsync(SyncBase @base, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(@base);
        return await _base.SaveAsync(
            new SyncBaseDto(
                @base.LastSyncedCommit?.ToString(),
                @base.LastRemoteTree?.ToString(),
                [.. @base.Manifest.Entries.Select(ToDto)]),
            ct).ConfigureAwait(false);
    }

    public async Task<SyncJournal?> LoadJournalAsync(CancellationToken ct)
    {
        var load = await _journal.LoadAsync(ct).ConfigureAwait(false);
        if (load.Status != DocumentLoadStatus.Loaded)
        {
            return null;
        }

        try
        {
            return new SyncJournal(
                load.Value.Attempt,
                load.Value.ExpectedHead is { } head ? CommitSha.Parse(head).Value : null,
                ToManifest(load.Value.Target),
                [.. load.Value.Actions.Select(action => new SyncJournalAction(
                    action.Kind,
                    VaultPath.Create(action.Path).Value,
                    action.Sha is { } sha ? BlobSha.Parse(sha).Value : null))],
                load.Value.StartedAt);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<Result<Unit>> SaveJournalAsync(SyncJournal journal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return await _journal.SaveAsync(
            new SyncJournalDto(
                journal.Attempt,
                journal.ExpectedHead?.ToString(),
                [.. journal.Target.Entries.Select(ToDto)],
                [.. journal.Actions.Select(action => new JournalActionDto(
                    action.Kind,
                    action.Path.ToString(),
                    action.Sha?.ToString()))],
                journal.StartedAt),
            ct).ConfigureAwait(false);
    }

    public Task<Result<Unit>> ClearJournalAsync(CancellationToken ct) => _journal.DeleteAsync(ct);

    public async Task<HashCache> LoadHashCacheAsync(CancellationToken ct)
    {
        var load = await _cache.LoadAsync(ct).ConfigureAwait(false);
        try
        {
            return HashCache.Create(load.Value.Entries.Select(entry => new HashCacheEntry(
                VaultPath.Create(entry.Path).Value,
                entry.Size,
                entry.ModifiedUtcTicks,
                BlobSha.Parse(entry.Sha).Value)));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return HashCache.Empty;
        }
    }

    public async Task<Result<Unit>> SaveHashCacheAsync(HashCache cache, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cache);
        return await _cache.SaveAsync(
            new HashCacheDto([.. cache.Entries.Select(entry => new HashCacheEntryDto(
                entry.Path.ToString(),
                entry.Size,
                entry.ModifiedUtcTicks,
                entry.Sha.ToString()))]),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteTreeCacheEntry>> LoadRemoteTreesAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_treesDirectory))
        {
            return [];
        }

        var loaded = new List<RemoteTreeDto>();
        foreach (var file in Directory.EnumerateFiles(_treesDirectory, "*.json"))
        {
            try
            {
                var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                if (JsonSerializer.Deserialize(text, PlatformJsonContext.Default.RemoteTreeDto) is { } dto)
                {
                    loaded.Add(dto);
                }
            }
            catch (IOException)
            {
            }
            catch (JsonException)
            {
            }
        }

        try
        {
            return [.. loaded
                .OrderBy(dto => dto.SavedAtUtcTicks)
                .TakeLast(MaxRemoteTrees)
                .Select(ToCacheEntry)];
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return [];
        }
    }

    public async Task<Result<Unit>> SaveRemoteTreesAsync(IReadOnlyList<RemoteTreeCacheEntry> trees, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(trees);
        try
        {
            Directory.CreateDirectory(_treesDirectory);
            var kept = trees.TakeLast(MaxRemoteTrees).ToList();
            var names = kept.Select(tree => tree.Tree + ".json").ToHashSet(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(_treesDirectory, "*.json"))
            {
                if (!names.Contains(Path.GetFileName(file)))
                {
                    File.Delete(file);
                }
            }

            var stamp = _time.GetUtcNow().UtcTicks;
            for (var index = 0; index < kept.Count; index++)
            {
                var entry = kept[index];
                var dto = new RemoteTreeDto(
                    entry.Tree.ToString(),
                    [.. entry.Snapshot.Files.Select(file => new ManifestEntryDto(file.Path.ToString(), file.Sha.ToString(), file.Size))],
                    [.. entry.Snapshot.Quarantined.Select(issue => new RemotePathIssueDto(issue.Path, issue.Reason))],
                    stamp + index);
                await WriteAtomicAsync(
                    Path.Combine(_treesDirectory, entry.Tree + ".json"),
                    dto,
                    PlatformJsonContext.Default.RemoteTreeDto,
                    ct).ConfigureAwait(false);
            }

            return Result.Success();
        }
        catch (IOException ex)
        {
            return new Error(ErrorCodes.StorageIo, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new Error(ErrorCodes.StorageIo, ex.Message);
        }
    }

    public async Task<IReadOnlyList<SyncLogEntry>> LoadLogAsync(int max, CancellationToken ct)
    {
        if (max <= 0 || !File.Exists(_logPath))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(_logPath, ct).ConfigureAwait(false);
        var entries = new List<SyncLogEntry>();
        foreach (var line in lines.TakeLast(max))
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                var dto = JsonSerializer.Deserialize(line, PlatformJsonContext.Default.SyncLogEntryDto);
                if (dto is not null)
                {
                    entries.Add(ToLogEntry(dto));
                }
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
            {
            }
        }

        return entries;
    }

    public async Task<Result<Unit>> AppendLogAsync(SyncLogEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _logGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            var line = JsonSerializer.Serialize(ToDto(entry), PlatformJsonContext.Default.SyncLogEntryDto);
            await File.AppendAllTextAsync(_logPath, line + "\n", Utf8, ct).ConfigureAwait(false);
            _logLines++;
            if (_logLines > MaxLogEntries)
            {
                var lines = await File.ReadAllLinesAsync(_logPath, ct).ConfigureAwait(false);
                var text = string.Join('\n', lines.TakeLast(MaxLogEntries)) + "\n";
                await WriteAtomicAsync(_logPath, text, ct).ConfigureAwait(false);
                _logLines = MaxLogEntries;
            }

            return Result.Success();
        }
        catch (IOException ex)
        {
            return new Error(ErrorCodes.StorageIo, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new Error(ErrorCodes.StorageIo, ex.Message);
        }
        finally
        {
            _logGate.Release();
        }
    }

    public void Dispose()
    {
        _base.Dispose();
        _cache.Dispose();
        _journal.Dispose();
        _logGate.Dispose();
    }

    private static ManifestEntryDto ToDto(ManifestEntry entry) =>
        new(entry.Path.ToString(), entry.Sha.ToString(), entry.Size);

    private static Manifest ToManifest(IReadOnlyList<ManifestEntryDto> entries) =>
        Manifest.Create(entries.Select(entry => new ManifestEntry(
            VaultPath.Create(entry.Path).Value,
            BlobSha.Parse(entry.Sha).Value,
            entry.Size))).Value;

    private static RemoteTreeCacheEntry ToCacheEntry(RemoteTreeDto dto) => new(
        TreeSha.Parse(dto.Tree).Value,
        new RemoteTreeSnapshot(
            [.. dto.Files.Select(file => new RemoteTreeFile(
                VaultPath.Create(file.Path).Value,
                BlobSha.Parse(file.Sha).Value,
                file.Size))],
            [.. dto.Quarantined.Select(issue => new RemotePathIssue(issue.Path, issue.Reason))]));

    private static SyncLogEntryDto ToDto(SyncLogEntry entry) => new(
        entry.When,
        entry.Outcome,
        entry.Head?.ToString(),
        entry.Downloaded,
        entry.Uploaded,
        entry.DeletedLocal,
        entry.DeletedRemote,
        entry.Conflicts,
        entry.Deferred,
        (long)entry.Duration.TotalMilliseconds,
        entry.ErrorCode);

    private static SyncLogEntry ToLogEntry(SyncLogEntryDto dto) => new(
        dto.When,
        dto.Outcome,
        dto.Head is { } head ? CommitSha.Parse(head).Value : null,
        dto.Downloaded,
        dto.Uploaded,
        dto.DeletedLocal,
        dto.DeletedRemote,
        dto.Conflicts,
        dto.Deferred,
        TimeSpan.FromMilliseconds(dto.DurationMilliseconds),
        dto.ErrorCode);

    private static async Task WriteAtomicAsync<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        var temp = $"{path}.~basalt-{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, json, Utf8, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private static async Task WriteAtomicAsync(string path, string text, CancellationToken ct)
    {
        var temp = $"{path}.~basalt-{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, text, Utf8, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Creates the JSON state store under the vault's local data folder.</summary>
internal sealed class JsonSyncStateStoreFactory(IVaultLocalDataStore localData, TimeProvider time) : ISyncStateStoreFactory
{
    public ISyncStateStore Create(VaultId vault) =>
        new JsonSyncStateStore(Path.Combine(localData.DirectoryOf(vault), "sync"), time);

    public Task<Result<Unit>> DeleteAsync(VaultId vault, CancellationToken ct)
    {
        try
        {
            var directory = Path.Combine(localData.DirectoryOf(vault), "sync");
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return Task.FromResult(Result.Success());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Result.Failure(new Error(ErrorCodes.StorageIo, exception.Message)));
        }
    }
}
