using System.Text;
using Basalt.Adapters.Platform.Json;
using Basalt.Application.Ports.Storage;
using Basalt.Domain.Notes;
using Basalt.Testing.Contracts;

namespace Basalt.Adapters.Platform.Tests.Json;

public sealed class JsonVaultConfigStoreTests : VaultConfigStoreContract
{
    private static readonly VaultPath ConfigPath = VaultPath.Create(".basalt/vault.json").Value;

    protected override Task<IVaultConfigStore> CreateSutAsync() => Task.FromResult<IVaultConfigStore>(new JsonVaultConfigStore());

    protected override Task CorruptAsync() =>
        Vault.WriteAllBytesAsync(ConfigPath, Encoding.UTF8.GetBytes("<<<<<<< Laptop\n{}\n=======\n{}\n>>>>>>> GitHub\n"), null, Ct);

    [Fact]
    public async Task Save_WritesIndentedJsonWithSchemaVersion()
    {
        await Sut.SaveVaultConfigAsync(Vault, Sample, Ct);

        var text = (await Vault.ReadAllTextAsync(ConfigPath, Ct)).Value;
        text.Should().StartWith("{\n  \"schemaVersion\": 1,\n  \"vaultId\": \"3f2c8a1e-0000-4000-8000-000000000001\"");
        text.Should().Contain("\"linkFormat\": \"Markdown\"").And.EndWith("}\n");
    }

    [Fact]
    [Trait("Spec", "F-001.4")]
    public async Task Load_DocumentFromAnotherDevice_KeepsVaultId()
    {
        await Vault.WriteAllBytesAsync(ConfigPath, Encoding.UTF8.GetBytes("""{"schemaVersion":1,"vaultId":"11111111-2222-3333-4444-555555555555","settings":{}}"""), null, Ct);

        var load = await Sut.LoadVaultConfigAsync(Vault, Ct);

        load.Value!.Id.ToString().Should().Be("11111111-2222-3333-4444-555555555555");
        load.Value.Settings.Should().BeEquivalentTo(Domain.Vaults.VaultSettings.Default);
    }

    [Theory]
    [InlineData("""{"schemaVersion":5,"vaultId":"11111111-2222-3333-4444-555555555555","settings":{}}""")]
    [InlineData("""{"schemaVersion":1,"vaultId":"not-a-guid","settings":{}}""")]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    public async Task Load_UnusableDocuments_AreInvalid(string json)
    {
        await Vault.WriteAllBytesAsync(ConfigPath, Encoding.UTF8.GetBytes(json), null, Ct);

        (await Sut.LoadVaultConfigAsync(Vault, Ct)).Status.Should().Be(DocumentLoadStatus.Invalid);
    }
}
