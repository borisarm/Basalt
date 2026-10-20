using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Testing.Contracts;
using Basalt.Testing.Fakes;

namespace Basalt.Application.Tests.Fakes;

public sealed class FakeClockContractTests : ClockContract
{
    protected override Task<IClock> CreateSutAsync() => Task.FromResult<IClock>(new FakeClock());
}

public sealed class SequentialIdGeneratorContractTests : IdGeneratorContract
{
    protected override Task<IIdGenerator> CreateSutAsync() => Task.FromResult<IIdGenerator>(new SequentialIdGenerator());
}

public sealed class FakeRandomContractTests : RandomContract
{
    protected override Task<IRandom> CreateSutAsync() => Task.FromResult<IRandom>(new FakeRandom());
}

public sealed class FakeCultureProviderContractTests : CultureProviderContract
{
    protected override Task<ICultureProvider> CreateSutAsync() => Task.FromResult<ICultureProvider>(new FakeCultureProvider());
}

public sealed class FakePlatformPathsContractTests : PlatformPathsContract
{
    protected override Task<IPlatformPaths> CreateSutAsync() => Task.FromResult<IPlatformPaths>(new FakePlatformPaths());
}

public sealed class FakeDeviceInfoContractTests : DeviceInfoContract
{
    protected override Task<IDeviceInfo> CreateSutAsync() => Task.FromResult<IDeviceInfo>(new FakeDeviceInfo());

    protected override Task<IDeviceInfo> ReopenAsync() => Task.FromResult<IDeviceInfo>(new FakeDeviceInfo());
}

public sealed class InMemoryVaultLocalDataStoreContractTests : VaultLocalDataStoreContract
{
    protected override Task<IVaultLocalDataStore> CreateSutAsync() => Task.FromResult<IVaultLocalDataStore>(new InMemoryVaultLocalDataStore());
}
