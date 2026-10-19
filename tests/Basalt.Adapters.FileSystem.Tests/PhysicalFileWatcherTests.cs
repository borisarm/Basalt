using System.Text;
using Basalt.Application.Ports.FileSystem;
using Basalt.Testing;
using Basalt.Testing.Contracts;

namespace Basalt.Adapters.FileSystem.Tests;

/// <summary>Integration tests on the real file system (generous timeouts, spec 09 §11).</summary>
public sealed class PhysicalFileWatcherTests : FileWatcherContract
{
    private readonly TempDirectory _directory = TempDirectory.Create("watch");

    protected override async Task<IVaultFileSystem> CreateSutAsync() =>
        (await new PhysicalVaultFileSystemFactory().OpenAsync(_directory.Path, Ct)).Value;

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _directory.DisposeAsync();
    }

    protected override Task ExternalWriteAsync(string path, string content)
    {
        _directory.WriteFile(path, content);
        return Task.CompletedTask;
    }

    protected override Task ExternalDeleteAsync(string path)
    {
        File.Delete(_directory.Combine(path.Split('/')));
        return Task.CompletedTask;
    }

    protected override Task ExternalRenameAsync(string from, string to)
    {
        File.Move(_directory.Combine(from.Split('/')), _directory.Combine(to.Split('/')));
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AtomicWritesThroughTheAdapter_AreReportedAsChangesOfTheTarget()
    {
        await ExternalWriteAsync("a.md", "v1");
        await using var watcher = Sut.CreateWatcher();

        await Sut.WriteAllBytesAsync(P("a.md"), Encoding.UTF8.GetBytes("v2"), null, Ct);

        var changes = await CollectAsync(watcher, c => c.Any(x => x.Path == P("a.md")), TimeSpan.FromMilliseconds(500));
        changes.Should().OnlyContain(c => c.Path == P("a.md"));
    }

    [Fact]
    public async Task BurstOfAThousandFiles_IsFullyReportedOrRequestsRescan()
    {
        await using var watcher = Sut.CreateWatcher();

        for (var i = 0; i < 1000; i++)
        {
            await File.WriteAllTextAsync(_directory.Combine($"n{i:D4}.md"), "x", Ct);
        }

        var changes = await CollectAsync(watcher, c => c.Any(x => x.Kind == FileSystemChangeKind.RescanRequired) || c.Select(x => x.Path).Distinct().Count() >= 1000);
        var rescan = changes.Any(c => c.Kind == FileSystemChangeKind.RescanRequired);
        (rescan || changes.Select(c => c.Path).Distinct().Count() == 1000).Should().BeTrue();
    }

    [Fact]
    public async Task Overflow_EmitsRescanRequired()
    {
        await using var watcher = (PhysicalFileWatcher)Sut.CreateWatcher();

        watcher.OnOverflow();

        var changes = await CollectAsync(watcher, c => c.Count > 0);
        changes.Should().ContainSingle().Which.Kind.Should().Be(FileSystemChangeKind.RescanRequired);
    }

    [Fact]
    public async Task Changes_InsideSubfolders_UseVaultPaths()
    {
        Directory.CreateDirectory(_directory.Combine("Sub", "Deep"));
        await using var watcher = Sut.CreateWatcher();

        await ExternalWriteAsync("Sub/Deep/n.md", "x");

        var changes = await CollectAsync(watcher, c => c.Any(x => x.Path == P("Sub/Deep/n.md")));
        changes.Should().Contain(c => c.Path == P("Sub/Deep/n.md"));
    }
}
