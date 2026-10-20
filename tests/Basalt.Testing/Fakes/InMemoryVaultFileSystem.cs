using System.Runtime.CompilerServices;
using System.Text;
using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Testing.Fakes;

/// <summary>
/// In-memory <see cref="IVaultFileSystem"/> (spec 03 §1.1): case-insensitive by default like Windows/macOS, with fault
/// injection (<see cref="FailNextWrite"/>) and simulation of external changes that are pushed to watchers.
/// </summary>
public sealed class InMemoryVaultFileSystem : IVaultFileSystem
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Error> _failNextWrite = new(StringComparer.Ordinal);
    private readonly List<ManualFileWatcher> _watchers = [];
    private readonly TimeProvider _time;

    public InMemoryVaultFileSystem(bool caseSensitive = false, TimeProvider? time = null, string rootLocation = "mem://vault")
    {
        IsCaseSensitive = caseSensitive;
        _time = time ?? TimeProvider.System;
        RootLocation = rootLocation;
    }

    public string RootLocation { get; }

    public bool IsCaseSensitive { get; }

    /// <summary>Number of successful writes (for assertions).</summary>
    public int WriteCount { get; private set; }

    /// <summary>The next write, move (as source) or delete of <paramref name="path"/> fails with <paramref name="error"/> and
    /// changes nothing.</summary>
    public void FailNextWrite(VaultPath path, Error error)
    {
        lock (_gate)
        {
            _failNextWrite[Key(path)] = error;
        }
    }

    /// <summary>Writes as another application would and notifies watchers.</summary>
    public void SimulateExternalWrite(VaultPath path, string content)
    {
        bool existed;
        lock (_gate)
        {
            existed = _entries.ContainsKey(Key(path));
            WriteUnlocked(path, Encoding.UTF8.GetBytes(content));
        }

        Notify(new FileSystemChange(existed ? FileSystemChangeKind.Changed : FileSystemChangeKind.Created, path));
    }

    public void SimulateExternalDelete(VaultPath path)
    {
        lock (_gate)
        {
            RemoveUnlocked(path);
        }

        Notify(new FileSystemChange(FileSystemChangeKind.Deleted, path));
    }

    public void SimulateExternalRename(VaultPath from, VaultPath to)
    {
        MoveAsync(from, to, CancellationToken.None).GetAwaiter().GetResult();
        Notify(new FileSystemChange(FileSystemChangeKind.Renamed, to, from));
    }

    public IFileWatcher CreateWatcher()
    {
        var watcher = new ManualFileWatcher();
        lock (_gate)
        {
            _watchers.Add(watcher);
        }

        return watcher;
    }

    public Task<Result<byte[]>> ReadAllBytesAsync(VaultPath path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(ReadUnlocked(path));
        }
    }

    public async Task<Result<string>> ReadAllTextAsync(VaultPath path, CancellationToken ct) =>
        (await ReadAllBytesAsync(path, ct)).Map(bytes => NoteContent.FromBytes(bytes).Text);

    public async Task<Result<Stream>> OpenReadAsync(VaultPath path, CancellationToken ct) =>
        (await ReadAllBytesAsync(path, ct)).Map<Stream>(bytes => new MemoryStream(bytes, writable: false));

    public Task<Result<FileStat>> StatAsync(VaultPath path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (path.IsRoot)
            {
                return Task.FromResult(Result.Success(new FileStat(0, DateTimeOffset.UnixEpoch, true)));
            }

            return Task.FromResult(_entries.TryGetValue(Key(path), out var entry)
                ? Result.Success(new FileStat(entry.Content.Length, entry.LastWrite, entry.IsDirectory))
                : Result.Failure<FileStat>(NotFound(path)));
        }
    }

    public Task<bool> ExistsAsync(VaultPath path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(path.IsRoot || _entries.ContainsKey(Key(path)));
        }
    }

    public Task<Result<Unit>> WriteAllBytesAsync(VaultPath path, ReadOnlyMemory<byte> content, BlobSha? expectedSha, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (TakeInjectedFailure(path) is { } failure)
            {
                return Task.FromResult(Result.Failure(failure));
            }

            if (expectedSha is { } expected)
            {
                if (!_entries.TryGetValue(Key(path), out var current) || current.IsDirectory || GitBlobHasher.Compute(current.Content) != expected)
                {
                    return Task.FromResult(Result.Failure(new Error(ErrorCodes.FsConcurrentModification, "The file changed since it was read.")));
                }
            }

            return Task.FromResult(WriteUnlocked(path, content.ToArray()));
        }
    }

    public Task<Result<Unit>> CreateNewAsync(VaultPath path, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (TakeInjectedFailure(path) is { } failure)
            {
                return Task.FromResult(Result.Failure(failure));
            }

            return Task.FromResult(_entries.ContainsKey(Key(path))
                ? Result.Failure(new Error(ErrorCodes.FsAlreadyExists, "The file already exists."))
                : WriteUnlocked(path, content.ToArray()));
        }
    }

    public Task<Result<Unit>> CreateDirectoryAsync(VaultPath path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(EnsureDirectory(path));
        }
    }

    public Task<Result<Unit>> MoveAsync(VaultPath from, VaultPath to, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_entries.TryGetValue(Key(from), out var source))
            {
                return Task.FromResult(Result.Failure(NotFound(from)));
            }

            if (TakeInjectedFailure(from) is { } failure)
            {
                return Task.FromResult(Result.Failure(failure));
            }

            var caseOnly = Key(from) == Key(to);
            if (!caseOnly && (to.IsRoot || _entries.ContainsKey(Key(to))))
            {
                return Task.FromResult(Result.Failure(new Error(ErrorCodes.FsAlreadyExists, "The target already exists.")));
            }

            var parent = EnsureDirectory(to.Parent);
            if (parent.IsFailure)
            {
                return Task.FromResult(parent);
            }

            var moved = _entries.Values.Where(e => e.Path == source.Path || e.Path.IsDescendantOf(source.Path)).ToList();
            foreach (var entry in moved)
            {
                _entries.Remove(Key(entry.Path));
            }

            foreach (var entry in moved)
            {
                var relative = entry.Path == source.Path ? string.Empty : entry.Path.RelativeTo(source.Path);
                var target = relative.Length == 0 ? to : to.Combine(relative).Value;
                _entries[Key(target)] = entry with { Path = target };
            }

            return Task.FromResult(Result.Success());
        }
    }

    public Task<Result<Unit>> DeleteAsync(VaultPath path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(TakeInjectedFailure(path) is { } failure ? Result.Failure(failure) : RemoveUnlocked(path));
        }
    }

    public Task<Result<Unit>> DeleteDirectoryIfEmptyAsync(VaultPath path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_entries.TryGetValue(Key(path), out var entry) && entry.IsDirectory && !_entries.Values.Any(e => e.Path.IsDescendantOf(entry.Path)))
            {
                _entries.Remove(Key(path));
            }

            return Task.FromResult(Result.Success());
        }
    }

    public async IAsyncEnumerable<FileEntry> EnumerateAsync(VaultPath root, bool recursive, [EnumeratorCancellation] CancellationToken ct)
    {
        List<Entry> snapshot;
        lock (_gate)
        {
            var actualRoot = root.IsRoot ? root : _entries.TryGetValue(Key(root), out var r) ? r.Path : root;
            snapshot = [.. _entries.Values
                .Where(e => e.Path.IsDescendantOf(actualRoot) && (recursive || e.Path.Parent == actualRoot))
                .Where(e => !VaultFileConventions.IsTemporaryFile(e.Path.Name))
                .OrderBy(e => e.Path)];
        }

        foreach (var entry in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new FileEntry(entry.Path, entry.IsDirectory, entry.Content.Length, entry.LastWrite);
        }
    }

    private Result<byte[]> ReadUnlocked(VaultPath path)
    {
        if (!_entries.TryGetValue(Key(path), out var entry))
        {
            return NotFound(path);
        }

        return entry.IsDirectory
            ? new Error(ErrorCodes.FsIsDirectory, "The path is a directory.")
            : entry.Content.ToArray();
    }

    private Result<Unit> WriteUnlocked(VaultPath path, byte[] content)
    {
        if (_entries.TryGetValue(Key(path), out var existing))
        {
            if (existing.IsDirectory)
            {
                return new Error(ErrorCodes.FsIsDirectory, "The path is a directory.");
            }

            // Like real case-insensitive file systems, an overwrite keeps the existing name.
            _entries[Key(path)] = existing with { Content = content, LastWrite = _time.GetUtcNow() };
            WriteCount++;
            return Result.Success();
        }

        var parent = EnsureDirectory(path.Parent);
        if (parent.IsFailure)
        {
            return parent;
        }

        _entries[Key(path)] = new Entry(path, false, content, _time.GetUtcNow());
        WriteCount++;
        return Result.Success();
    }

    private Result<Unit> EnsureDirectory(VaultPath path)
    {
        if (path.IsRoot)
        {
            return Result.Success();
        }

        if (_entries.TryGetValue(Key(path), out var existing))
        {
            return existing.IsDirectory ? Result.Success() : new Error(ErrorCodes.FsNotADirectory, "A parent of the path is a file.");
        }

        var parent = EnsureDirectory(path.Parent);
        if (parent.IsFailure)
        {
            return parent;
        }

        _entries[Key(path)] = new Entry(path, true, [], _time.GetUtcNow());
        return Result.Success();
    }

    private Result<Unit> RemoveUnlocked(VaultPath path)
    {
        if (!_entries.TryGetValue(Key(path), out var entry))
        {
            return NotFound(path);
        }

        foreach (var descendant in _entries.Values.Where(e => e.Path.IsDescendantOf(entry.Path)).ToList())
        {
            _entries.Remove(Key(descendant.Path));
        }

        _entries.Remove(Key(path));
        return Result.Success();
    }

    private Error? TakeInjectedFailure(VaultPath path) =>
        _failNextWrite.Remove(Key(path), out var error) ? error : null;

    private void Notify(FileSystemChange change)
    {
        List<ManualFileWatcher> watchers;
        lock (_gate)
        {
            watchers = [.. _watchers];
        }

        foreach (var watcher in watchers)
        {
            watcher.Publish(change);
        }
    }

    private string Key(VaultPath path) => IsCaseSensitive ? path.ToString() : path.CaseFoldKey;

    private static Error NotFound(VaultPath path) =>
        new Error(ErrorCodes.FsNotFound, "The file or folder does not exist.").With("path", path.ToString());

    private sealed record Entry(VaultPath Path, bool IsDirectory, byte[] Content, DateTimeOffset LastWrite);
}
