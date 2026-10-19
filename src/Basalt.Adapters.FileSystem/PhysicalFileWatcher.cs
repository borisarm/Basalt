using Basalt.Application.Ports.FileSystem;

namespace Basalt.Adapters.FileSystem;

/// <summary>Placeholder until T2.6 implements watching with debounce and rescan.</summary>
internal sealed class PhysicalFileWatcher(string root) : IFileWatcher
{
    public string Root { get; } = root;

    public IAsyncEnumerable<FileSystemChange> WatchAsync(CancellationToken ct) => throw new NotSupportedException("Implemented in T2.6.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
