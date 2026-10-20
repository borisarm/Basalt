using System.Text;
using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Domain.Notes;

namespace Basalt.Testing.Contracts;

/// <summary>
/// Behaviour of <see cref="ISystemTrash"/> (spec 03 §5, <c>DeleteMode.SystemTrash</c>): where available, entries leave
/// the vault; where not, <c>fs.trash_unavailable</c> is returned and nothing is touched.
/// </summary>
public abstract class SystemTrashContract : ContractTest<ISystemTrash>
{
    /// <summary>The vault whose entries are trashed (same storage the trash acts on).</summary>
    protected abstract IVaultFileSystem Vault { get; }

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    [Fact]
    public async Task MoveToTrash_File()
    {
        await Vault.WriteAllBytesAsync(P("A.md"), Encoding.UTF8.GetBytes("a"), null, Ct);

        var result = await Sut.MoveToTrashAsync(Vault.RootLocation, P("A.md"), Ct);

        await ExpectTrashedAsync(result, P("A.md"));
    }

    [Fact]
    public async Task MoveToTrash_FolderWithContent()
    {
        await Vault.WriteAllBytesAsync(P("X/Sub/a.md"), Encoding.UTF8.GetBytes("a"), null, Ct);

        var result = await Sut.MoveToTrashAsync(Vault.RootLocation, P("X"), Ct);

        await ExpectTrashedAsync(result, P("X"));
    }

    [Fact]
    public async Task MoveToTrash_Missing_Fails()
    {
        var result = await Sut.MoveToTrashAsync(Vault.RootLocation, P("none.md"), Ct);

        result.Error.Code.Should().Be(Sut.IsAvailable ? ErrorCodes.FsNotFound : ErrorCodes.FsTrashUnavailable);
    }

    private async Task ExpectTrashedAsync(Domain.Common.Result<Domain.Common.Unit> result, VaultPath path)
    {
        if (Sut.IsAvailable)
        {
            result.IsSuccess.Should().BeTrue();
            (await Vault.ExistsAsync(path, Ct)).Should().BeFalse();
        }
        else
        {
            result.Error.Code.Should().Be(ErrorCodes.FsTrashUnavailable);
            (await Vault.ExistsAsync(path, Ct)).Should().BeTrue();
        }
    }
}
