using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Testing;
using Basalt.Testing.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Basalt.Adapters.Platform.Tests;

/// <summary>The real platform services resolved from the adapter's registration, over a temporary AppData.</summary>
internal sealed class PlatformServices : IAsyncDisposable
{
    private readonly TempDirectory _directory = TempDirectory.Create("platform");

    public PlatformServices() => Provider = Build();

    public ServiceProvider Provider { get; private set; }

    public T Get<T>()
        where T : notnull => Provider.GetRequiredService<T>();

    /// <summary>A new container over the same AppData (the app started again).</summary>
    public async Task RestartAsync()
    {
        await Provider.DisposeAsync();
        Provider = Build();
    }

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        await _directory.DisposeAsync();
    }

    private ServiceProvider Build() => new ServiceCollection().AddBasaltPlatformAdapter(_directory.Path).BuildServiceProvider();
}

public sealed class SystemClockTests : ClockContract
{
    private readonly PlatformServices _services = new();

    protected override Task<IClock> CreateSutAsync() => Task.FromResult(_services.Get<IClock>());

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _services.DisposeAsync();
    }
}

public sealed class GuidIdGeneratorTests : IdGeneratorContract
{
    private readonly PlatformServices _services = new();

    protected override Task<IIdGenerator> CreateSutAsync() => Task.FromResult(_services.Get<IIdGenerator>());

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _services.DisposeAsync();
    }
}

public sealed class SystemRandomTests : RandomContract
{
    private readonly PlatformServices _services = new();

    protected override Task<IRandom> CreateSutAsync() => Task.FromResult(_services.Get<IRandom>());

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _services.DisposeAsync();
    }
}

public sealed class SystemCultureContractTests : CultureProviderContract
{
    private readonly PlatformServices _services = new();

    protected override Task<ICultureProvider> CreateSutAsync() => Task.FromResult(_services.Get<ICultureProvider>());

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _services.DisposeAsync();
    }
}

public sealed class DesktopPlatformPathsTests : PlatformPathsContract
{
    private readonly PlatformServices _services = new();

    protected override Task<IPlatformPaths> CreateSutAsync() => Task.FromResult(_services.Get<IPlatformPaths>());

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _services.DisposeAsync();
    }
}

public sealed class DesktopDeviceInfoTests : DeviceInfoContract
{
    private readonly PlatformServices _services = new();

    protected override Task<IDeviceInfo> CreateSutAsync() => Task.FromResult(_services.Get<IDeviceInfo>());

    protected override async Task<IDeviceInfo> ReopenAsync()
    {
        _ = Sut.DeviceId;
        await _services.RestartAsync();
        return _services.Get<IDeviceInfo>();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _services.DisposeAsync();
    }
}

public sealed class VaultLocalDataStoreTests : VaultLocalDataStoreContract
{
    private readonly PlatformServices _services = new();

    protected override Task<IVaultLocalDataStore> CreateSutAsync() => Task.FromResult(_services.Get<IVaultLocalDataStore>());

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _services.DisposeAsync();
    }
}
