using System.Text.Json.Nodes;
using Basalt.Adapters.Platform.Json;
using Basalt.Application.Accounts;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Settings;
using Basalt.Application.Vaults;
using Basalt.Domain.Accounts;
using Basalt.Domain.Vaults;
using Basalt.Testing;
using Basalt.Testing.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace Basalt.Adapters.Platform.Tests.Json;

public sealed class JsonFileStoreTests : KeyValueStoreContract<VaultRegistryDocument>
{
    private readonly TempDirectory _directory = TempDirectory.Create("store");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 19, 15, 42, 33, TimeSpan.Zero));

    private string FilePath => _directory.Combine("vaults.json");

    protected override VaultRegistryDocument DefaultValue => VaultRegistryDocument.Empty;

    protected override (VaultRegistryDocument First, VaultRegistryDocument Second) Samples =>
    (
        new VaultRegistryDocument([new VaultRegistration(new VaultId(Guid.Parse("00000000-0000-0000-0000-000000000001")), "Personal", "C:/v/P", DateTimeOffset.UnixEpoch)]),
        new VaultRegistryDocument([new VaultRegistration(new VaultId(Guid.Parse("00000000-0000-0000-0000-000000000002")), "Trabajo", "/v/T", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(2))])
    );

    protected override Task<IDocumentStore<VaultRegistryDocument>> CreateSutAsync() =>
        Task.FromResult<IDocumentStore<VaultRegistryDocument>>(Create());

    protected override Task CorruptAsync() => File.WriteAllTextAsync(FilePath, "{ not json", Ct);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _directory.DisposeAsync();
    }

    private JsonFileStore<VaultRegistryDocument> Create(int version = 1, IReadOnlyList<JsonMigration>? migrations = null, bool indented = false) =>
        new(FilePath, PlatformJsonContext.Default.VaultRegistryDocument, () => VaultRegistryDocument.Empty, version, migrations ?? [], indented, _time);

    [Fact]
    [Trait("Spec", "PERS-1.1")]
    public async Task Save_WritesSchemaVersionFirst_CompactCamelCase()
    {
        await Sut.SaveAsync(Samples.First, Ct);

        var json = await File.ReadAllTextAsync(FilePath, Ct);
        json.Should().StartWith("{\"schemaVersion\":1,\"vaults\":[{\"id\":\"00000000-0000-0000-0000-000000000001\"");
        json.Should().NotContain("\n");
    }

    [Fact]
    public async Task Indented_IsUsedForVaultFiles()
    {
        using var store = Create(indented: true);

        await store.SaveAsync(Samples.First, Ct);

        (await File.ReadAllTextAsync(FilePath, Ct)).Should().Contain("\n  \"vaults\"");
    }

    [Fact]
    [Trait("Spec", "PERS-1.1")]
    public async Task Migrations_AreAppliedInOrder()
    {
        await File.WriteAllTextAsync(FilePath, """{"schemaVersion":1,"list":[{"id":"00000000-0000-0000-0000-000000000001","name":"Old","rootLocation":"/o","addedAt":"1970-01-01T00:00:00+00:00"}]}""", Ct);
        var renameList = new JsonMigration(1, root =>
        {
            root["vaults"] = root["list"]!.DeepClone();
            root.Remove("list");
            return root;
        });
        var addSuffix = new JsonMigration(2, root =>
        {
            foreach (var vault in root["vaults"]!.AsArray())
            {
                vault!["name"] = vault["name"]!.GetValue<string>() + " (v3)";
            }

            return root;
        });
        using var store = Create(version: 3, migrations: [addSuffix, renameList]);

        var load = await store.LoadAsync(Ct);

        load.Status.Should().Be(DocumentLoadStatus.Loaded);
        load.Value.Vaults.Single().Name.Should().Be("Old (v3)");
    }

    [Fact]
    public async Task MissingMigration_IsInvalid()
    {
        await File.WriteAllTextAsync(FilePath, """{"schemaVersion":1,"vaults":[]}""", Ct);
        using var store = Create(version: 2);

        (await store.LoadAsync(Ct)).Status.Should().Be(DocumentLoadStatus.Invalid);
    }

    [Fact]
    public async Task NewerSchema_IsInvalid_AndBackedUpBeforeOverwrite()
    {
        const string Newer = """{"schemaVersion":9,"vaults":[],"future":true}""";
        await File.WriteAllTextAsync(FilePath, Newer, Ct);

        var load = await Sut.LoadAsync(Ct);
        await Sut.SaveAsync(Samples.First, Ct);

        load.Status.Should().Be(DocumentLoadStatus.Invalid);
        load.Problem.Should().Contain("newer");
        var backup = Directory.EnumerateFiles(_directory.Path, "vaults.json.corrupt-*").Single();
        Path.GetFileName(backup).Should().Be("vaults.json.corrupt-20261019154233000");
        (await File.ReadAllTextAsync(backup, Ct)).Should().Be(Newer);
    }

    [Fact]
    [Trait("Spec", "PERS-1.1")]
    public async Task CorruptFile_IsNotOverwrittenByLoading()
    {
        await File.WriteAllTextAsync(FilePath, "<<<<<<< conflict", Ct);

        await Sut.LoadAsync(Ct);
        await Sut.LoadAsync(Ct);

        (await File.ReadAllTextAsync(FilePath, Ct)).Should().Be("<<<<<<< conflict");
    }

    [Fact]
    public async Task NonObjectDocument_IsInvalid()
    {
        await File.WriteAllTextAsync(FilePath, "[1,2]", Ct);

        (await Sut.LoadAsync(Ct)).Status.Should().Be(DocumentLoadStatus.Invalid);
    }

    [Fact]
    public async Task Save_LeavesNoTemporaryFiles()
    {
        await Sut.SaveAsync(Samples.First, Ct);
        await Sut.SaveAsync(Samples.Second, Ct);

        Directory.EnumerateFiles(_directory.Path).Select(Path.GetFileName).Should().Equal("vaults.json");
    }

    [Fact]
    public async Task OtherDocuments_RoundTripWithConverters()
    {
        var accountsPath = _directory.Combine("accounts.json");
        using var accounts = new JsonFileStore<AccountsDocument>(accountsPath, PlatformJsonContext.Default.AccountsDocument, () => AccountsDocument.Empty, 1, [], false, _time);
        var account = new GitHubAccount(new GitHubAccountId(Guid.Parse("00000000-0000-0000-0000-0000000000aa")), "boris", GitHubHost.Create("https://ghe.example.com").Value, new Uri("https://avatars/x.png"), ["repo", "read:user"], DateTimeOffset.UnixEpoch);
        using var settings = new JsonFileStore<AppSettings>(_directory.Combine("settings.json"), PlatformJsonContext.Default.AppSettings, () => AppSettings.Default, 1, [], false, _time);
        var custom = AppSettings.Default with
        {
            Theme = ThemeMode.Dark,
            LastVaultId = new VaultId(Guid.Parse("00000000-0000-0000-0000-000000000005")),
            Hotkeys = new Dictionary<string, string> { ["app.quickSwitcher"] = "Ctrl+O" },
        };

        await accounts.SaveAsync(new AccountsDocument([account]), Ct);
        await settings.SaveAsync(custom, Ct);

        (await accounts.LoadAsync(Ct)).Value.Accounts.Single().Should().BeEquivalentTo(account);
        (await settings.LoadAsync(Ct)).Value.Should().BeEquivalentTo(custom);
        (await File.ReadAllTextAsync(accountsPath, Ct)).Should().Contain("\"host\":\"https://ghe.example.com\"").And.NotContain("token");
        JsonNode.Parse(await File.ReadAllTextAsync(_directory.Combine("settings.json"), Ct))!["theme"]!.GetValue<string>().Should().Be("Dark");
    }
}
