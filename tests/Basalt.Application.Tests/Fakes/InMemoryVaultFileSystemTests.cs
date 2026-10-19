using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Domain.Common;
using Basalt.Testing.Contracts;
using Basalt.Testing.Fakes;

namespace Basalt.Application.Tests.Fakes;

public sealed class InMemoryVaultFileSystemTests : VaultFileSystemContract
{
    protected override Task<IVaultFileSystem> CreateSutAsync() => Task.FromResult<IVaultFileSystem>(new InMemoryVaultFileSystem());

    [Fact]
    public async Task FailNextWrite_FailsOnceWithoutWriting()
    {
        var fs = (InMemoryVaultFileSystem)Sut;
        var error = new Error(ErrorCodes.FsIo, "disk full");
        fs.FailNextWrite(P("a.md"), error);

        (await Write("a.md", "x")).Error.Should().Be(error);
        (await Sut.ExistsAsync(P("a.md"), Ct)).Should().BeFalse();
        (await Write("a.md", "x")).IsSuccess.Should().BeTrue();
        fs.WriteCount.Should().Be(1);
    }

    [Fact]
    public async Task SimulatedExternalChanges_ReachWatchers()
    {
        var fs = (InMemoryVaultFileSystem)Sut;
        await using var watcher = fs.CreateWatcher();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        fs.SimulateExternalWrite(P("ext.md"), "hi");
        fs.SimulateExternalWrite(P("ext.md"), "again");
        fs.SimulateExternalDelete(P("ext.md"));

        var changes = new List<FileSystemChange>();
        await foreach (var change in watcher.WatchAsync(cts.Token))
        {
            changes.Add(change);
            if (changes.Count == 3)
            {
                break;
            }
        }

        changes.Select(c => c.Kind).Should().Equal(FileSystemChangeKind.Created, FileSystemChangeKind.Changed, FileSystemChangeKind.Deleted);
    }

    [Fact]
    public async Task CaseSensitiveMode_KeepsBothNames()
    {
        var fs = new InMemoryVaultFileSystem(caseSensitive: true);

        await fs.WriteAllBytesAsync(P("a.md"), B("1"), null, Ct);
        await fs.WriteAllBytesAsync(P("A.md"), B("2"), null, Ct);

        (await fs.ReadAllTextAsync(P("a.md"), Ct)).Value.Should().Be("1");
        (await fs.ReadAllTextAsync(P("A.md"), Ct)).Value.Should().Be("2");
    }

    [Fact]
    public async Task WriteUnderAFile_IsNotADirectory()
    {
        await Write("file", "x");

        (await Write("file/child.md", "y")).Error.Code.Should().Be(ErrorCodes.FsNotADirectory);
    }
}
