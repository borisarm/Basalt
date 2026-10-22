using Basalt.Application.Ports.Platform;
using Basalt.Testing.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Basalt.Adapters.Platform.Tests;

public sealed class SystemFontScaleProviderContract : FontScaleProviderContract
{
    protected override Task<IFontScaleProvider> CreateSutAsync() =>
        Task.FromResult<IFontScaleProvider>(new ServiceCollection()
            .AddBasaltPlatformAdapter(Path.Combine(Path.GetTempPath(), "basalt-fontscale-" + Guid.NewGuid().ToString("N")))
            .BuildServiceProvider()
            .GetRequiredService<IFontScaleProvider>());
}
