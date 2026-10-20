using System.Text.Json.Nodes;
using Basalt.Adapters.FileSystem;
using Basalt.Adapters.Platform;
using Basalt.Application.Events;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Vaults;
using Basalt.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basalt.Sync.IntegrationTests.Vaults;

/// <summary>Vault use cases against the real file system and JSON stores (T3.2).</summary>
public sealed class VaultUseCasesIntegrationTests : IAsyncDisposable
{
    private readonly TempDirectory _root = TempDirectory.Create("vaults");
    private readonly ServiceProvider _services;

    public VaultUseCasesIntegrationTests()
    {
        _services = new ServiceCollection()
            .AddBasaltFileSystemAdapter()
            .AddBasaltPlatformAdapter(Path.Combine(_root.Path, "appdata"))
            .BuildServiceProvider();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private T Get<T>()
        where T : notnull => _services.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _root.DisposeAsync();
    }

    [Fact]
    [Trait("Spec", "F-001.1")]
    public async Task CreateOpenRemove_OnRealDisk()
    {
        using var registry = new VaultRegistry(Get<IDocumentStore<VaultRegistryDocument>>());
        await using var sessions = new VaultSessions();
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var create = new CreateVaultHandler(Get<IVaultFileSystemFactory>(), Get<IVaultConfigStore>(), registry, Get<IIdGenerator>(), Get<IClock>());

        var created = (await create.HandleAsync(new CreateVault("Notas", _root.Path), Ct)).Value;

        var vaultJson = JsonNode.Parse(await File.ReadAllTextAsync(_root.Combine("Notas", ".basalt", "vault.json"), Ct))!;
        vaultJson["vaultId"]!.GetValue<string>().Should().Be(created.Id.ToString());
        File.Exists(_root.Combine("appdata", "vaults.json")).Should().BeTrue();

        var open = new OpenVaultHandler(registry, Get<IVaultFileSystemFactory>(), Get<IVaultConfigStore>(), sessions, [], events, Get<IClock>());
        (await open.HandleAsync(new OpenVault(created.Id), Ct)).Value.RootLocation.Should().Be(_root.Combine("Notas"));

        Directory.CreateDirectory(Path.Combine(Get<IVaultLocalDataStore>().DirectoryOf(created.Id), "sync"));
        var remove = new RemoveVaultFromListHandler(registry, sessions, Get<IVaultLocalDataStore>(), events);
        (await remove.HandleAsync(new RemoveVaultFromList(created.Id), Ct)).IsSuccess.Should().BeTrue();

        Directory.Exists(Get<IVaultLocalDataStore>().DirectoryOf(created.Id)).Should().BeFalse();
        File.Exists(_root.Combine("Notas", ".basalt", "vault.json")).Should().BeTrue("removing from the list never deletes vault files");
    }

    [Fact]
    [Trait("Spec", "F-001.3")]
    public async Task OpenFolderAsVault_OnRealDisk_CreatesVaultJson()
    {
        _root.WriteFile("Existing/nota.md", "# Nota");
        using var registry = new VaultRegistry(Get<IDocumentStore<VaultRegistryDocument>>());
        var handler = new OpenFolderAsVaultHandler(Get<IVaultFileSystemFactory>(), Get<IVaultConfigStore>(), registry, Get<IIdGenerator>(), Get<IClock>());

        var registration = (await handler.HandleAsync(new OpenFolderAsVault(_root.Combine("Existing")), Ct)).Value;

        registration.Name.Should().Be("Existing");
        File.Exists(_root.Combine("Existing", ".basalt", "vault.json")).Should().BeTrue();
        File.ReadAllText(_root.Combine("Existing", "nota.md")).Should().Be("# Nota");
    }
}
