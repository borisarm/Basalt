using System.Text;
using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Testing;
using Basalt.Testing.Contracts;

namespace Basalt.Adapters.FileSystem.Tests;

public sealed class PhysicalVaultFileSystemTests : VaultFileSystemContract
{
    private readonly TempDirectory _directory = TempDirectory.Create("fs");

    private PhysicalVaultFileSystem Physical => (PhysicalVaultFileSystem)Sut;

    protected override async Task<IVaultFileSystem> CreateSutAsync() =>
        (await new PhysicalVaultFileSystemFactory().OpenAsync(_directory.Path, Ct)).Value;

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _directory.DisposeAsync();
    }

    [Fact]
    [Trait("Spec", "NFR-021")]
    public async Task Write_InterruptedBeforeRename_KeepsOriginalAndCleansTemp()
    {
        await Write("a.md", "original");
        Physical.BeforeCommit = _ => throw new IOException("power loss");

        var result = await Write("a.md", "new content");

        result.Error.Code.Should().Be(ErrorCodes.FsIo);
        File.ReadAllText(_directory.Combine("a.md")).Should().Be("original");
        Directory.EnumerateFiles(_directory.Path).Select(Path.GetFileName).Should().Equal("a.md");
    }

    [Fact]
    public async Task Write_WhenCrashLeavesATempFile_ItIsNeverEnumerated()
    {
        await Write("a.md", "x");
        File.WriteAllText(_directory.Combine(".~basalt-0123.tmp"), "orphan");

        (await List()).Should().Equal("a.md");
    }

    [Fact]
    public async Task NfdNamesOnDisk_AreAddressedWithNfcPaths()
    {
        var nfd = "Reunión.md";
        File.WriteAllText(_directory.Combine(nfd), "macos");

        (await Sut.ReadAllTextAsync(P("Reunión.md"), Ct)).Value.Should().Be("macos");
        (await List()).Should().Equal("Reunión.md");
    }

    [Fact]
    public void Factory_ProbesCaseSensitivity()
    {
        var expectedInsensitive = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        PhysicalVaultFileSystemFactory.ProbeCaseSensitivity(_directory.Path).Should().Be(!expectedInsensitive);
        Directory.EnumerateFileSystemEntries(_directory.Path).Should().BeEmpty("the probe file is removed");
    }

    [Fact]
    public async Task Factory_MissingFolder_IsNotFound()
    {
        var result = await new PhysicalVaultFileSystemFactory().OpenAsync(_directory.Combine("missing"), Ct);

        result.Error.Code.Should().Be(ErrorCodes.FsNotFound);
    }

    [Fact]
    [Trait("OS", "Windows")]
    public async Task ExclusivelyLockedFile_ReportsIoError()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await Write("locked.md", "x");
        await using var locker = new FileStream(_directory.Combine("locked.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        (await Sut.ReadAllBytesAsync(P("locked.md"), Ct)).Error.Code.Should().Be(ErrorCodes.FsIo);
        (await Write("locked.md", "y")).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Write_IsDurable_BytesExactlyOnDisk()
    {
        var bytes = Encoding.UTF8.GetBytes("línea\r\nsegunda\r\n");

        await Sut.WriteAllBytesAsync(P("crlf.md"), bytes, null, Ct);

        File.ReadAllBytes(_directory.Combine("crlf.md")).Should().Equal(bytes);
    }

    [Fact]
    public async Task UnaddressableNames_AreSkippedByEnumeration()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // Windows/macOS do not allow these names to be created anyway.
        }

        File.WriteAllText(_directory.Combine("a:b.md"), "x");
        await Write("ok.md", "x");

        (await List()).Should().Equal("ok.md");
    }

    [Fact]
    public async Task CreateNew_RacingWriter_DoesNotOverwrite()
    {
        Physical.BeforeCommit = _ =>
        {
            File.WriteAllText(_directory.Combine("race.md"), "someone else");
            return Task.CompletedTask;
        };

        var result = await Sut.CreateNewAsync(P("race.md"), Encoding.UTF8.GetBytes("mine"), Ct);

        result.Error.Code.Should().Be(ErrorCodes.FsAlreadyExists);
        File.ReadAllText(_directory.Combine("race.md")).Should().Be("someone else");
    }
}
