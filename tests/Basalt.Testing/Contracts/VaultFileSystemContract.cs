using System.Text;
using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Testing.Contracts;

/// <summary>Behaviour every <see cref="IVaultFileSystem"/> must have (spec 03 §1.1). Fake and real adapter inherit it.</summary>
public abstract class VaultFileSystemContract : ContractTest<IVaultFileSystem>
{
    protected static VaultPath P(string path) => VaultPath.Create(path).Value;

    protected static byte[] B(string text) => Encoding.UTF8.GetBytes(text);

    protected Task<Domain.Common.Result<Domain.Common.Unit>> Write(string path, string text) =>
        Sut.WriteAllBytesAsync(P(path), B(text), null, Ct);

    protected async Task<List<string>> List(string root = "", bool recursive = true)
    {
        var result = new List<string>();
        await foreach (var entry in Sut.EnumerateAsync(P(root), recursive, Ct))
        {
            result.Add(entry.Path + (entry.IsDirectory ? "/" : string.Empty));
        }

        return [.. result.Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task Write_ThenRead_RoundTripsBytes()
    {
        byte[] bytes = [0, 1, 2, 255, 0xEF, 0xBB, 0xBF];

        (await Sut.WriteAllBytesAsync(P("bin/data.bin"), bytes, null, Ct)).IsSuccess.Should().BeTrue();

        (await Sut.ReadAllBytesAsync(P("bin/data.bin"), Ct)).Value.Should().Equal(bytes);
    }

    [Fact]
    public async Task Write_CreatesParentFolders()
    {
        await Write("a/b/c/note.md", "x");

        (await Sut.StatAsync(P("a/b"), Ct)).Value.IsDirectory.Should().BeTrue();
    }

    [Fact]
    public async Task Write_Overwrites()
    {
        await Write("n.md", "one");
        await Write("n.md", "two");

        (await Sut.ReadAllTextAsync(P("n.md"), Ct)).Value.Should().Be("two");
    }

    [Fact]
    public async Task ReadAllText_DropsBom_AndDecodesUtf8()
    {
        await Sut.WriteAllBytesAsync(P("bom.md"), (byte[])[0xEF, 0xBB, 0xBF, .. B("¡hola!")], null, Ct);

        (await Sut.ReadAllTextAsync(P("bom.md"), Ct)).Value.Should().Be("¡hola!");
    }

    [Fact]
    public async Task Read_Missing_IsNotFound()
    {
        (await Sut.ReadAllBytesAsync(P("missing.md"), Ct)).Error.Code.Should().Be(ErrorCodes.FsNotFound);
        (await Sut.ReadAllTextAsync(P("missing.md"), Ct)).Error.Code.Should().Be(ErrorCodes.FsNotFound);
        (await Sut.OpenReadAsync(P("missing.md"), Ct)).Error.Code.Should().Be(ErrorCodes.FsNotFound);
        (await Sut.StatAsync(P("missing.md"), Ct)).Error.Code.Should().Be(ErrorCodes.FsNotFound);
        (await Sut.ExistsAsync(P("missing.md"), Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Read_Directory_IsAnError()
    {
        await Sut.CreateDirectoryAsync(P("folder"), Ct);

        (await Sut.ReadAllBytesAsync(P("folder"), Ct)).Error.Code.Should().Be(ErrorCodes.FsIsDirectory);
    }

    [Fact]
    public async Task OpenRead_StreamsContent()
    {
        await Write("s.md", "stream me");

        await using var stream = (await Sut.OpenReadAsync(P("s.md"), Ct)).Value;
        using var reader = new StreamReader(stream);

        (await reader.ReadToEndAsync(Ct)).Should().Be("stream me");
    }

    [Fact]
    public async Task Stat_ReportsSizeAndKind()
    {
        await Write("sized.md", "12345");

        var stat = (await Sut.StatAsync(P("sized.md"), Ct)).Value;

        stat.Size.Should().Be(5);
        stat.IsDirectory.Should().BeFalse();
        stat.LastWriteUtc.Should().BeAfter(DateTimeOffset.MinValue);
        (await Sut.ExistsAsync(P("sized.md"), Ct)).Should().BeTrue();
        (await Sut.StatAsync(VaultPath.Root, Ct)).Value.IsDirectory.Should().BeTrue();
    }

    [Fact]
    [Trait("Spec", "NFR-021")]
    public async Task Write_WithMatchingExpectedSha_Succeeds()
    {
        await Write("c.md", "v1");

        var result = await Sut.WriteAllBytesAsync(P("c.md"), B("v2"), GitBlobHasher.Compute(B("v1")), Ct);

        result.IsSuccess.Should().BeTrue();
        (await Sut.ReadAllTextAsync(P("c.md"), Ct)).Value.Should().Be("v2");
    }

    [Fact]
    [Trait("Spec", "SYNC-7.1")]
    public async Task Write_WithStaleExpectedSha_FailsAndKeepsContent()
    {
        await Write("c.md", "edited by user");

        var result = await Sut.WriteAllBytesAsync(P("c.md"), B("from sync"), GitBlobHasher.Compute(B("old")), Ct);

        result.Error.Code.Should().Be(ErrorCodes.FsConcurrentModification);
        (await Sut.ReadAllTextAsync(P("c.md"), Ct)).Value.Should().Be("edited by user");
    }

    [Fact]
    public async Task Write_WithExpectedSha_OnMissingFile_IsConcurrentModification()
    {
        var result = await Sut.WriteAllBytesAsync(P("gone.md"), B("x"), GitBlobHasher.Compute(B("old")), Ct);

        result.Error.Code.Should().Be(ErrorCodes.FsConcurrentModification);
        (await Sut.ExistsAsync(P("gone.md"), Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Write_OverADirectory_Fails()
    {
        await Sut.CreateDirectoryAsync(P("dir.md"), Ct);

        (await Write("dir.md", "x")).Error.Code.Should().Be(ErrorCodes.FsIsDirectory);
    }

    [Fact]
    public async Task CreateNew_FailsWhenTheFileExists()
    {
        (await Sut.CreateNewAsync(P("new.md"), B("first"), Ct)).IsSuccess.Should().BeTrue();

        (await Sut.CreateNewAsync(P("new.md"), B("second"), Ct)).Error.Code.Should().Be(ErrorCodes.FsAlreadyExists);
        (await Sut.ReadAllTextAsync(P("new.md"), Ct)).Value.Should().Be("first");
    }

    [Fact]
    public async Task CreateDirectory_IsIdempotent()
    {
        (await Sut.CreateDirectoryAsync(P("x/y"), Ct)).IsSuccess.Should().BeTrue();
        (await Sut.CreateDirectoryAsync(P("x/y"), Ct)).IsSuccess.Should().BeTrue();

        (await List()).Should().Equal("x/", "x/y/");
    }

    [Fact]
    public async Task Move_File_ToNewFolder()
    {
        await Write("A.md", "content");

        (await Sut.MoveAsync(P("A.md"), P("Archivo/A.md"), Ct)).IsSuccess.Should().BeTrue();

        (await Sut.ExistsAsync(P("A.md"), Ct)).Should().BeFalse();
        (await Sut.ReadAllTextAsync(P("Archivo/A.md"), Ct)).Value.Should().Be("content");
    }

    [Fact]
    public async Task Move_Directory_MovesItsContent()
    {
        await Write("X/a.md", "a");
        await Write("X/sub/b.md", "b");

        (await Sut.MoveAsync(P("X"), P("Y"), Ct)).IsSuccess.Should().BeTrue();

        (await List()).Should().Equal("Y/", "Y/a.md", "Y/sub/", "Y/sub/b.md");
    }

    [Fact]
    [Trait("Spec", "F-002.3")]
    public async Task Move_OntoExistingTarget_FailsWithoutChanges()
    {
        await Write("A.md", "a");
        await Write("B.md", "b");

        (await Sut.MoveAsync(P("A.md"), P("B.md"), Ct)).Error.Code.Should().Be(ErrorCodes.FsAlreadyExists);

        (await Sut.ReadAllTextAsync(P("A.md"), Ct)).Value.Should().Be("a");
        (await Sut.ReadAllTextAsync(P("B.md"), Ct)).Value.Should().Be("b");
    }

    [Fact]
    [Trait("Spec", "F-002.4")]
    public async Task Move_CaseOnlyRename_Works()
    {
        await Write("nota.md", "x");

        (await Sut.MoveAsync(P("nota.md"), P("Nota.md"), Ct)).IsSuccess.Should().BeTrue();

        (await List()).Should().Equal("Nota.md");
    }

    [Fact]
    public async Task Move_MissingSource_IsNotFound()
    {
        (await Sut.MoveAsync(P("nope.md"), P("x.md"), Ct)).Error.Code.Should().Be(ErrorCodes.FsNotFound);
    }

    [Fact]
    public async Task Delete_FileAndFolder()
    {
        await Write("f.md", "x");
        await Write("d/a.md", "x");

        (await Sut.DeleteAsync(P("f.md"), Ct)).IsSuccess.Should().BeTrue();
        (await Sut.DeleteAsync(P("d"), Ct)).IsSuccess.Should().BeTrue();

        (await List()).Should().BeEmpty();
        (await Sut.DeleteAsync(P("f.md"), Ct)).Error.Code.Should().Be(ErrorCodes.FsNotFound);
    }

    [Fact]
    public async Task DeleteIfUnchanged_OnlyDeletesWhenTheContentMatches()
    {
        await Write("a.md", "one");
        var changed = GitBlobHasher.Compute("two"u8);
        var current = GitBlobHasher.Compute("one"u8);

        (await Sut.DeleteIfUnchangedAsync(P("a.md"), changed, Ct)).Error.Code
            .Should().Be(ErrorCodes.FsConcurrentModification);
        (await Sut.ExistsAsync(P("a.md"), Ct)).Should().BeTrue();

        (await Sut.DeleteIfUnchangedAsync(P("a.md"), current, Ct)).IsSuccess.Should().BeTrue();
        (await Sut.ExistsAsync(P("a.md"), Ct)).Should().BeFalse();
        (await Sut.DeleteIfUnchangedAsync(P("a.md"), current, Ct)).Error.Code.Should().Be(ErrorCodes.FsNotFound);
    }

    [Fact]
    public async Task DeleteDirectoryIfEmpty_OnlyDeletesEmptyFolders()
    {
        await Sut.CreateDirectoryAsync(P("empty"), Ct);
        await Write("full/a.md", "x");

        (await Sut.DeleteDirectoryIfEmptyAsync(P("empty"), Ct)).IsSuccess.Should().BeTrue();
        (await Sut.DeleteDirectoryIfEmptyAsync(P("full"), Ct)).IsSuccess.Should().BeTrue();
        (await Sut.DeleteDirectoryIfEmptyAsync(P("never"), Ct)).IsSuccess.Should().BeTrue();

        (await List()).Should().Equal("full/", "full/a.md");
    }

    [Fact]
    public async Task Enumerate_RecursiveAndShallow_IncludesHiddenEntries()
    {
        await Write("a.md", "x");
        await Write(".basalt/vault.json", "{}");
        await Write("dir/b.md", "x");
        await Write("dir/deep/c.md", "x");

        (await List()).Should().Equal(".basalt/", ".basalt/vault.json", "a.md", "dir/", "dir/b.md", "dir/deep/", "dir/deep/c.md");
        (await List(recursive: false)).Should().Equal(".basalt/", "a.md", "dir/");
        (await List("dir", recursive: false)).Should().Equal("dir/b.md", "dir/deep/");
    }

    [Fact]
    public async Task Enumerate_ReportsSizes()
    {
        await Write("s.md", "123");

        await foreach (var entry in Sut.EnumerateAsync(VaultPath.Root, true, Ct))
        {
            entry.Size.Should().Be(3);
        }
    }

    [Fact]
    public async Task Write_LeavesNoTemporaryFiles()
    {
        for (var i = 0; i < 5; i++)
        {
            await Write("busy.md", $"v{i}");
        }

        (await List()).Should().Equal("busy.md");
    }

    [Fact]
    public async Task CaseInsensitiveFileSystems_ResolvePathsIgnoringCase()
    {
        if (Sut.IsCaseSensitive)
        {
            return;
        }

        await Write("Readme.md", "one");
        await Write("README.md", "two");

        (await List()).Should().Equal("Readme.md");
        (await Sut.ReadAllTextAsync(P("readme.MD"), Ct)).Value.Should().Be("two");
    }

    [Fact]
    public async Task Unicode_Names_RoundTripInNfc()
    {
        await Write("Reunión 😀/Café.md", "x");

        (await List()).Should().Equal("Reunión 😀/", "Reunión 😀/Café.md");
    }
}
