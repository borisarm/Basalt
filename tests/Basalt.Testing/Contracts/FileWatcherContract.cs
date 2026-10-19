using Basalt.Application.Ports.FileSystem;
using Basalt.Domain.Notes;

namespace Basalt.Testing.Contracts;

/// <summary>
/// Expected watcher events after external operations (spec 03 §3). Implementations provide the file system and the
/// "external" operations (another application touching the vault).
/// </summary>
public abstract class FileWatcherContract : ContractTest<IVaultFileSystem>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    protected static VaultPath P(string path) => VaultPath.Create(path).Value;

    protected abstract Task ExternalWriteAsync(string path, string content);

    protected abstract Task ExternalDeleteAsync(string path);

    protected abstract Task ExternalRenameAsync(string from, string to);

    /// <summary>Collects events until <paramref name="done"/> holds or the (generous) timeout expires.</summary>
    protected async Task<List<FileSystemChange>> CollectAsync(IFileWatcher watcher, Func<List<FileSystemChange>, bool> done, TimeSpan? quietAfter = null)
    {
        var changes = new List<FileSystemChange>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(Timeout);
        try
        {
            await foreach (var change in watcher.WatchAsync(cts.Token))
            {
                changes.Add(change);
                if (done(changes))
                {
                    if (quietAfter is null)
                    {
                        break;
                    }

                    cts.CancelAfter(quietAfter.Value);
                }
            }
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
        }

        return changes;
    }

    [Fact]
    [Trait("Spec", "F-002.7")]
    public async Task ExternalCreate_IsReported()
    {
        await using var watcher = Sut.CreateWatcher();

        await ExternalWriteAsync("Externa.md", "hola");

        var changes = await CollectAsync(watcher, c => c.Any(x => x.Path == P("Externa.md")));
        changes.Should().Contain(c => c.Path == P("Externa.md") && (c.Kind == FileSystemChangeKind.Created || c.Kind == FileSystemChangeKind.Changed));
    }

    [Fact]
    public async Task ExternalModify_IsReported()
    {
        await ExternalWriteAsync("m.md", "v1");
        await using var watcher = Sut.CreateWatcher();

        await ExternalWriteAsync("m.md", "v2");

        var changes = await CollectAsync(watcher, c => c.Any(x => x.Path == P("m.md")));
        changes.Should().Contain(c => c.Path == P("m.md") && c.Kind == FileSystemChangeKind.Changed);
    }

    [Fact]
    public async Task ExternalDelete_IsReported()
    {
        await ExternalWriteAsync("d.md", "x");
        await using var watcher = Sut.CreateWatcher();

        await ExternalDeleteAsync("d.md");

        var changes = await CollectAsync(watcher, c => c.Any(x => x.Kind == FileSystemChangeKind.Deleted));
        changes.Should().Contain(new FileSystemChange(FileSystemChangeKind.Deleted, P("d.md")));
    }

    [Fact]
    public async Task ExternalRename_IsReportedWithOldPath()
    {
        await ExternalWriteAsync("old.md", "x");
        await using var watcher = Sut.CreateWatcher();

        await ExternalRenameAsync("old.md", "new.md");

        var changes = await CollectAsync(watcher, c => c.Any(x => x.Kind == FileSystemChangeKind.Renamed));
        changes.Should().Contain(new FileSystemChange(FileSystemChangeKind.Renamed, P("new.md"), P("old.md")));
    }

    [Fact]
    public async Task TemporaryFiles_AreNeverReported()
    {
        await using var watcher = Sut.CreateWatcher();

        await ExternalWriteAsync(".~basalt-abc.tmp", "temp");
        await ExternalWriteAsync("real.md", "x");

        var changes = await CollectAsync(watcher, c => c.Any(x => x.Path == P("real.md")), TimeSpan.FromMilliseconds(500));
        changes.Should().NotContain(c => VaultFileConventions.IsTemporaryFile(c.Path.Name));
    }
}
