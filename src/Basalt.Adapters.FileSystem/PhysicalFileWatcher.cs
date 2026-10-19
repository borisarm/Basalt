using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Basalt.Application.Ports.FileSystem;

namespace Basalt.Adapters.FileSystem;

/// <summary>
/// <see cref="IFileWatcher"/> over <see cref="FileSystemWatcher"/> (spec 03 §1.1): notifications are coalesced and
/// emitted after 200 ms without new events; buffer overflows produce <see cref="FileSystemChangeKind.RescanRequired"/>.
/// </summary>
internal sealed class PhysicalFileWatcher : IFileWatcher
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    private readonly string _root;
    private readonly Lock _gate = new();
    private readonly ChangeCoalescer _coalescer = new();
    private readonly Channel<FileSystemChange> _output = Channel.CreateUnbounded<FileSystemChange>();
    private readonly FileSystemWatcher _watcher;
    private readonly ITimer _timer;

    public PhysicalFileWatcher(string root, TimeProvider? time = null)
    {
        _root = root;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => FlushPending(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, e) => Raw(FileSystemChangeKind.Created, e.FullPath);
        _watcher.Changed += (_, e) => Raw(FileSystemChangeKind.Changed, e.FullPath);
        _watcher.Deleted += (_, e) => Raw(FileSystemChangeKind.Deleted, e.FullPath);
        _watcher.Renamed += (_, e) => Raw(FileSystemChangeKind.Renamed, e.FullPath, e.OldFullPath);
        _watcher.Error += (_, _) => OnOverflow();
        _watcher.EnableRaisingEvents = true;
    }

    public async IAsyncEnumerable<FileSystemChange> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (await _output.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (_output.Reader.TryRead(out var change))
            {
                yield return change;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        await _timer.DisposeAsync().ConfigureAwait(false);
        _output.Writer.TryComplete();
    }

    /// <summary>Overflow handler (also used by tests: real overflows are hard to provoke deterministically).</summary>
    internal void OnOverflow()
    {
        lock (_gate)
        {
            _coalescer.Overflow();
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Raw(FileSystemChangeKind kind, string fullPath, string? oldFullPath = null)
    {
        lock (_gate)
        {
            _coalescer.Add(kind, Relative(fullPath), Relative(oldFullPath));
            if (_coalescer.HasPending)
            {
                _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void FlushPending()
    {
        IReadOnlyList<FileSystemChange> changes;
        lock (_gate)
        {
            changes = _coalescer.Flush();
        }

        foreach (var change in changes)
        {
            _output.Writer.TryWrite(change);
        }
    }

    private string? Relative(string? fullPath)
    {
        if (fullPath is null)
        {
            return null;
        }

        // macOS reports /var and /tmp through their real location (/private/var, /private/tmp).
        var root = !_root.StartsWith("/private/", StringComparison.Ordinal) && fullPath.StartsWith("/private" + _root, StringComparison.Ordinal)
            ? "/private" + _root
            : _root;
        return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }
}
