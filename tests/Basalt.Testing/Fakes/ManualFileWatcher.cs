using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Basalt.Application.Ports.FileSystem;

namespace Basalt.Testing.Fakes;

/// <summary>Watcher fake: tests push changes with <see cref="Publish"/>.</summary>
public sealed class ManualFileWatcher : IFileWatcher
{
    private readonly Channel<FileSystemChange> _channel = Channel.CreateUnbounded<FileSystemChange>();

    public void Publish(FileSystemChange change) => _channel.Writer.TryWrite(change);

    public async IAsyncEnumerable<FileSystemChange> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (await _channel.Reader.WaitToReadAsync(ct))
        {
            while (_channel.Reader.TryRead(out var change))
            {
                yield return change;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
