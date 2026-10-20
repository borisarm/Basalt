using System.Globalization;
using Basalt.Adapters.FileSystem;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Testing;
using Basalt.Testing.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Basalt.Adapters.Platform.Tests;

/// <summary>The real recycle bin over a temporary folder (on Windows the test files end up in the recycle bin).</summary>
public sealed class SystemTrashTests : SystemTrashContract
{
    private readonly TempDirectory _directory = TempDirectory.Create("trash");
    private IVaultFileSystem? _vault;

    protected override IVaultFileSystem Vault => _vault!;

    protected override async Task<ISystemTrash> CreateSutAsync()
    {
        var services = new ServiceCollection().AddBasaltFileSystemAdapter().AddBasaltPlatformAdapter(_directory.Path).BuildServiceProvider();
        _vault = (await services.GetRequiredService<IVaultFileSystemFactory>().CreateAsync(Path.Combine(_directory.Path, "vault"), Ct)).Value;
        return services.GetRequiredService<ISystemTrash>();
    }

    [Fact]
    public void IsAvailableOnlyOnWindows() => Sut.IsAvailable.Should().Be(OperatingSystem.IsWindows());

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _directory.DisposeAsync();
    }
}

public sealed class SystemCultureProviderTests
{
    [Fact]
    public void ReturnsTheCurrentCulture()
    {
        var culture = new ServiceCollection().AddBasaltPlatformAdapter().BuildServiceProvider().GetRequiredService<ICultureProvider>();

        culture.Current.Should().BeSameAs(CultureInfo.CurrentCulture);
    }
}
