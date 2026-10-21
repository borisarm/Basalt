using Basalt.Adapters.Platform.Json;
using Basalt.Application.Ports.Storage;
using Basalt.Testing;
using Basalt.Testing.Contracts;

namespace Basalt.Adapters.Platform.Tests.Json;

public sealed class JsonSyncStateStoreTests : SyncStateStoreContract
{
    private readonly TempDirectory _directory = TempDirectory.Create("sync-state");

    protected override Task<ISyncStateStore> CreateSutAsync() =>
        Task.FromResult<ISyncStateStore>(new JsonSyncStateStore(Path.Combine(_directory.Path, "sync"), TimeProvider.System));

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _directory.DisposeAsync();
    }
}
