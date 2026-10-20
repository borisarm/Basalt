using Basalt.Application.Ports.Storage;
using Basalt.Application.Vaults;
using Basalt.Domain.Vaults;
using Basalt.Testing.Contracts;
using Basalt.Testing.Fakes;

namespace Basalt.Application.Tests.Fakes;

public sealed class InMemoryStoreTests : KeyValueStoreContract<VaultRegistryDocument>
{
    protected override VaultRegistryDocument DefaultValue => VaultRegistryDocument.Empty;

    protected override (VaultRegistryDocument First, VaultRegistryDocument Second) Samples => StoreSamples.Registries;

    protected override Task<IDocumentStore<VaultRegistryDocument>> CreateSutAsync() =>
        Task.FromResult<IDocumentStore<VaultRegistryDocument>>(new InMemoryStore<VaultRegistryDocument>(() => VaultRegistryDocument.Empty));

    protected override Task CorruptAsync()
    {
        ((InMemoryStore<VaultRegistryDocument>)Sut).Corrupt();
        return Task.CompletedTask;
    }
}

public sealed class InMemoryVaultConfigStoreTests : VaultConfigStoreContract
{
    protected override Task<IVaultConfigStore> CreateSutAsync() => Task.FromResult<IVaultConfigStore>(new InMemoryVaultConfigStore());

    protected override Task CorruptAsync()
    {
        ((InMemoryVaultConfigStore)Sut).Corrupt(Vault);
        return Task.CompletedTask;
    }

    protected override Task CorruptBookmarksAsync()
    {
        ((InMemoryVaultConfigStore)Sut).CorruptBookmarks(Vault);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryRecentFilesStoreTests : RecentFilesStoreContract
{
    protected override Task<IRecentFilesStore> CreateSutAsync() => Task.FromResult<IRecentFilesStore>(new InMemoryRecentFilesStore());

    protected override Task CorruptAsync()
    {
        ((InMemoryRecentFilesStore)Sut).Corrupt(VaultA);
        return Task.CompletedTask;
    }
}

internal static class StoreSamples
{
    public static (VaultRegistryDocument, VaultRegistryDocument) Registries { get; } =
    (
        new VaultRegistryDocument([new VaultRegistration(new VaultId(Guid.Parse("00000000-0000-0000-0000-000000000001")), "Personal", "C:/vaults/Personal", DateTimeOffset.UnixEpoch)]),
        new VaultRegistryDocument(
        [
            new VaultRegistration(new VaultId(Guid.Parse("00000000-0000-0000-0000-000000000002")), "Trabajo", "/Users/x/Trabajo", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1)),
            new VaultRegistration(new VaultId(Guid.Parse("00000000-0000-0000-0000-000000000003")), "Notas", "/n", DateTimeOffset.UnixEpoch),
        ])
    );
}
