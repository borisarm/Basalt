using Basalt.Application.Ports.Storage;
using Basalt.Testing.Contracts;
using Basalt.Testing.Fakes;

namespace Basalt.Adapters.Platform.Tests;

public sealed class InMemorySyncProfileStoreTests : SyncProfileStoreContract
{
    protected override Task<ISyncProfileStore> CreateSutAsync() => Task.FromResult<ISyncProfileStore>(new InMemorySyncProfileStore());
}
