using Basalt.Application.Ports.Storage;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;

namespace Basalt.Testing.Contracts;

/// <summary>Behaviour of <see cref="IRecentFilesStore"/> (spec 08 §1, F-011).</summary>
public abstract class RecentFilesStoreContract : ContractTest<IRecentFilesStore>
{
    protected static readonly VaultId VaultA = new(new Guid("aaaaaaaa-0000-4000-8000-000000000001"));
    protected static readonly VaultId VaultB = new(new Guid("bbbbbbbb-0000-4000-8000-000000000002"));

    /// <summary>Leaves the stored list of <see cref="VaultA"/> unreadable.</summary>
    protected abstract Task CorruptAsync();

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    [Fact]
    public async Task Missing_IsEmpty()
    {
        (await Sut.LoadAsync(VaultA, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task RoundTrips_InOrder_PerVault()
    {
        (await Sut.SaveAsync(VaultA, [P("b.md"), P("Carpeta/á.md"), P("a.md")], Ct)).IsSuccess.Should().BeTrue();

        (await Sut.LoadAsync(VaultA, Ct)).Should().Equal(P("b.md"), P("Carpeta/á.md"), P("a.md"));
        (await Sut.LoadAsync(VaultB, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Corrupt_IsEmpty_AndCanBeOverwritten()
    {
        await Sut.SaveAsync(VaultA, [P("a.md")], Ct);
        await CorruptAsync();

        (await Sut.LoadAsync(VaultA, Ct)).Should().BeEmpty();
        await Sut.SaveAsync(VaultA, [P("b.md")], Ct);
        (await Sut.LoadAsync(VaultA, Ct)).Should().Equal(P("b.md"));
    }
}
