using Basalt.Application.Ports.Storage;
using Basalt.Testing.Contracts;
using Basalt.Testing.Fakes;

namespace Basalt.Adapters.Platform.Tests;

public sealed class InMemorySyncStateStoreTests : SyncStateStoreContract
{
    protected override Task<ISyncStateStore> CreateSutAsync() => Task.FromResult<ISyncStateStore>(new InMemorySyncStateStore());
}
