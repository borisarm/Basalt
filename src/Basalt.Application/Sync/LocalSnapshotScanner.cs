using System.Globalization;
using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Application.Sync;

/// <summary>
/// Enumerates the local vault, applies the exclusion rules and hashes files with the hash cache
/// (spec 06 §6, step 1). A file whose size or modification time is unchanged is not read again.
/// </summary>
public sealed class LocalSnapshotScanner
{
    private readonly IVaultFileSystem _fileSystem;
    private readonly bool _isCaseSensitive;
    private readonly SyncScope _scope;
    private readonly SyncPolicy _policy;
    private readonly IgnoreRules _rules;

    /// <param name="rules">Vault-wide rules (defaults plus <c>.basalt/syncignore</c>); profile extras are appended here.</param>
    public LocalSnapshotScanner(IVaultFileSystem fileSystem, SyncScope scope, SyncPolicy policy, IgnoreRules rules)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(rules);
        _fileSystem = fileSystem;
        _isCaseSensitive = fileSystem.IsCaseSensitive;
        _scope = scope;
        _policy = policy;
        _rules = rules.Then(IgnoreRules.Parse(scope.ExtraIgnorePatterns ?? []));
    }

    /// <param name="hashCache">Previous cache; <c>null</c> or <see cref="HashCache.Empty"/> hashes everything.</param>
    /// <param name="quarantined">Remote paths that cannot be materialised on this device (spec 06 §7.4 and §7.5).</param>
    public async Task<LocalSnapshot> ScanAsync(
        HashCache? hashCache,
        IReadOnlyCollection<VaultPath>? quarantined,
        CancellationToken ct)
    {
        var cache = hashCache ?? HashCache.Empty;
        var incidents = new List<SyncIncident>();
        var excluded = new SortedSet<VaultPath>();
        var candidates = new List<FileEntry>();

        await foreach (var entry in _fileSystem.EnumerateAsync(VaultPath.Root, recursive: true, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsSymbolicLink)
            {
                excluded.Add(entry.Path);
                incidents.Add(new SyncIncident(SyncIncidentKind.SymbolicLink, entry.Path));
                continue;
            }

            if (entry.IsDirectory)
            {
                continue;
            }

            if (IsExcluded(entry.Path, quarantined))
            {
                excluded.Add(entry.Path);
                continue;
            }

            if (entry.Size > _policy.MaxFileSizeBytes)
            {
                excluded.Add(entry.Path);
                incidents.Add(new SyncIncident(
                    SyncIncidentKind.TooLarge,
                    entry.Path,
                    Detail: _policy.MaxFileSizeBytes.ToString(CultureInfo.InvariantCulture)));
                continue;
            }

            candidates.Add(entry);
        }

        if (_isCaseSensitive)
        {
            candidates = RemoveCaseCollisions(candidates, incidents, excluded);
        }

        candidates.Sort((a, b) => a.Path.CompareTo(b.Path));

        var entries = new List<ManifestEntry>(candidates.Count);
        var cacheEntries = new List<HashCacheEntry>(candidates.Count);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var ticks = candidate.LastWriteUtc.UtcTicks;
            if (!cache.TryGet(candidate.Path, candidate.Size, ticks, out var sha))
            {
                if (await TryHashAsync(candidate, incidents, excluded, ct).ConfigureAwait(false) is not { } hashed)
                {
                    continue;
                }

                sha = hashed;
            }

            entries.Add(new ManifestEntry(candidate.Path, sha, candidate.Size));
            cacheEntries.Add(new HashCacheEntry(candidate.Path, candidate.Size, ticks, sha));
        }

        return new LocalSnapshot(
            Manifest.Create(entries).Value,
            HashCache.Create(cacheEntries),
            incidents,
            [.. excluded]);
    }

    private async Task<BlobSha?> TryHashAsync(
        FileEntry entry,
        List<SyncIncident> incidents,
        SortedSet<VaultPath> excluded,
        CancellationToken ct)
    {
        var opened = await _fileSystem.OpenReadAsync(entry.Path, ct).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            excluded.Add(entry.Path);
            incidents.Add(new SyncIncident(SyncIncidentKind.Unreadable, entry.Path, Detail: opened.Error.Code));
            return null;
        }

        var stream = opened.Value;
        try
        {
            return await GitBlobHasher.ComputeAsync(stream, entry.Size, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            excluded.Add(entry.Path);
            incidents.Add(new SyncIncident(SyncIncidentKind.ChangedWhileScanning, entry.Path));
            return null;
        }
        catch (IOException)
        {
            excluded.Add(entry.Path);
            incidents.Add(new SyncIncident(SyncIncidentKind.Unreadable, entry.Path, Detail: ErrorCodes.FsIo));
            return null;
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static List<FileEntry> RemoveCaseCollisions(
        List<FileEntry> candidates,
        List<SyncIncident> incidents,
        SortedSet<VaultPath> excluded)
    {
        var kept = new List<FileEntry>(candidates.Count);
        foreach (var group in candidates.GroupBy(entry => entry.Path.CaseFoldKey, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(entry => entry.Path).ToList();
            kept.Add(ordered[0]);
            foreach (var dropped in ordered.Skip(1))
            {
                excluded.Add(dropped.Path);
                incidents.Add(new SyncIncident(SyncIncidentKind.CaseCollision, dropped.Path, ordered[0].Path));
            }
        }

        return kept;
    }

    private bool IsExcluded(VaultPath path, IReadOnlyCollection<VaultPath>? quarantined) =>
        SyncExclusions.IsIgnored(path, _scope, _rules)
        || (quarantined is not null
            && quarantined.Any(candidate => path == candidate || path.IsDescendantOf(candidate)));
}
