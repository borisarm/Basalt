using System.Text;
using Basalt.Adapters.Platform.Json;
using Basalt.Application.Ports.Storage;
using Basalt.Domain.Notes;
using Basalt.Testing.Contracts;

namespace Basalt.Adapters.Platform.Tests.Json;

public sealed class JsonVaultConfigStoreTests : VaultConfigStoreContract
{
    private const string ConflictedBookmarks = "<<<<<<< Laptop\n{\"items\":[]}\n=======\n{}\n>>>>>>> GitHub\n";
    private static readonly VaultPath ConfigPath = VaultPath.Create(".basalt/vault.json").Value;
    private static readonly VaultPath BookmarksPath = VaultPath.Create(".basalt/bookmarks.json").Value;

    protected override Task<IVaultConfigStore> CreateSutAsync() => Task.FromResult<IVaultConfigStore>(new JsonVaultConfigStore());

    protected override Task CorruptAsync() =>
        Vault.WriteAllBytesAsync(ConfigPath, Encoding.UTF8.GetBytes("<<<<<<< Laptop\n{}\n=======\n{}\n>>>>>>> GitHub\n"), null, Ct);

    protected override Task CorruptBookmarksAsync() =>
        Vault.WriteAllBytesAsync(BookmarksPath, Encoding.UTF8.GetBytes(ConflictedBookmarks), null, Ct);

    [Fact]
    [Trait("Spec", "PERS-1.1")]
    public async Task SavingOverInvalidBookmarks_KeepsTheInvalidFileAside()
    {
        await CorruptBookmarksAsync();
        await Vault.WriteAllBytesAsync(VaultPath.Create(".basalt/bookmarks.invalid.json").Value, "older"u8.ToArray(), null, Ct);

        await Sut.SaveBookmarksAsync(Vault, SampleBookmarks, Ct);

        (await Vault.ReadAllTextAsync(VaultPath.Create(".basalt/bookmarks.invalid 2.json").Value, Ct)).Value.Should().Be(ConflictedBookmarks);
        (await Sut.LoadBookmarksAsync(Vault, Ct)).Value.Should().BeEquivalentTo(SampleBookmarks);
    }

    [Fact]
    public async Task Bookmarks_UseAnObsidianLikeShape()
    {
        await Sut.SaveBookmarksAsync(Vault, SampleBookmarks, Ct);

        var text = (await Vault.ReadAllTextAsync(BookmarksPath, Ct)).Value;

        text.Should().StartWith("{\n  \"schemaVersion\": 1,\n  \"items\": [\n    {\n      \"type\": \"file\",\n      \"path\": \"Proyectos/A.md\"\n    },");
        text.Should().Contain("\"type\": \"group\"").And.Contain("\"type\": \"search\"").And.Contain("\"query\": \"tag:#urgente\"").And.EndWith("}\n");
    }

    [Fact]
    public async Task Bookmarks_FromANewerVersion_AreInvalid_AndUnknownTypesAreKept()
    {
        await Vault.WriteAllBytesAsync(BookmarksPath, Encoding.UTF8.GetBytes("""{"schemaVersion":9,"items":[]}"""), null, Ct);
        (await Sut.LoadBookmarksAsync(Vault, Ct)).Status.Should().Be(DocumentLoadStatus.Invalid);

        await Vault.WriteAllBytesAsync(BookmarksPath, Encoding.UTF8.GetBytes("""{"items":[{"type":"canvas","path":"x.canvas"},{"type":"file","path":"a.md"}]}"""), null, Ct);
        var load = await Sut.LoadBookmarksAsync(Vault, Ct);
        load.Status.Should().Be(DocumentLoadStatus.Invalid);
        load.Problem.Should().Contain("canvas");
    }

    [Fact]
    public async Task Workspace_FileIsNamedAfterTheDevice()
    {
        await Sut.SaveWorkspaceAsync(Vault, DeviceA, SampleWorkspace, Ct);

        (await Vault.ExistsAsync(VaultPath.Create($".basalt/workspace.{DeviceA}.json").Value, Ct)).Should().BeTrue();
        await Vault.WriteAllBytesAsync(VaultPath.Create($".basalt/workspace.{DeviceB}.json").Value, "not json"u8.ToArray(), null, Ct);
        (await Sut.LoadWorkspaceAsync(Vault, DeviceB, Ct)).Status.Should().Be(DocumentLoadStatus.Invalid);
    }

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
