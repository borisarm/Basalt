using Basalt.Application.Ports.Storage;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;

namespace Basalt.Testing.Contracts;

/// <summary>Behaviour of <see cref="IVaultConfigStore"/> over an in-memory vault.</summary>
public abstract class VaultConfigStoreContract : ContractTest<IVaultConfigStore>
{
    protected InMemoryVaultFileSystem Vault { get; } = new();

    protected abstract Task CorruptAsync();

    protected static VaultConfig Sample { get; } = new(
        new VaultId(new Guid("3f2c8a1e-0000-4000-8000-000000000001")),
        VaultSettings.Default with { AttachmentFolder = "Adjuntos", LinkFormat = LinkFormat.Markdown, DailyNotes = new DailyNotesSettings("Diario", "YYYY/YYYY-MM-DD") });

    [Fact]
    public async Task Missing_IsReported()
    {
        var load = await Sut.LoadVaultConfigAsync(Vault, Ct);

        load.Status.Should().Be(DocumentLoadStatus.Missing);
        load.Value.Should().BeNull();
    }

    [Fact]
    [Trait("Spec", "F-001.1")]
    public async Task Save_ThenLoad_RoundTrips()
    {
        (await Sut.SaveVaultConfigAsync(Vault, Sample, Ct)).IsSuccess.Should().BeTrue();

        var load = await Sut.LoadVaultConfigAsync(Vault, Ct);

        load.Status.Should().Be(DocumentLoadStatus.Loaded);
        load.Value.Should().BeEquivalentTo(Sample);
    }

    [Fact]
    public async Task Corrupt_IsInvalidAndNotOverwritten()
    {
        await Sut.SaveVaultConfigAsync(Vault, Sample, Ct);
        await CorruptAsync();

        var load = await Sut.LoadVaultConfigAsync(Vault, Ct);

        load.Status.Should().Be(DocumentLoadStatus.Invalid);
        load.Value.Should().BeNull();
    }
}
