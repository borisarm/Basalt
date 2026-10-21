using Basalt.Adapters.Platform.Json;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Storage;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;
using Basalt.Testing;
using Basalt.Testing.Contracts;

namespace Basalt.Adapters.Platform.Tests.Json;

public sealed class JsonSyncProfileStoreTests : SyncProfileStoreContract
{
    private readonly TempDirectory _directory = TempDirectory.Create("sync-profile");

    protected override Task<ISyncProfileStore> CreateSutAsync() =>
        Task.FromResult<ISyncProfileStore>(new JsonSyncProfileStore(new LocalData(_directory.Path), TimeProvider.System));

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _directory.DisposeAsync();
    }

    private sealed class LocalData(string root) : IVaultLocalDataStore
    {
        public string DirectoryOf(VaultId vault)
        {
            var path = Path.Combine(root, vault.Value.ToString("D"));
            Directory.CreateDirectory(path);
            return path;
        }

        public Task<Result<Unit>> DeleteAsync(VaultId vault, CancellationToken ct)
        {
            var path = DirectoryOf(vault);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return Task.FromResult(Result.Success());
        }
    }
}
