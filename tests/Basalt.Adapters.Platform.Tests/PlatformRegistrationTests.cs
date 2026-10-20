using Basalt.Application.Accounts;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Settings;
using Basalt.Application.Vaults;
using Basalt.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Basalt.Adapters.Platform.Tests;

public sealed class PlatformRegistrationTests
{
    [Fact]
    public async Task Registration_ResolvesStoresUnderTheAppDataFolder()
    {
        await using var directory = TempDirectory.Create("appdata");
        await using var provider = new ServiceCollection().AddBasaltPlatformAdapter(directory.Path).BuildServiceProvider(validateScopes: true);

        var paths = provider.GetRequiredService<IPlatformPaths>();
        var registry = provider.GetRequiredService<IDocumentStore<VaultRegistryDocument>>();
        await registry.SaveAsync(VaultRegistryDocument.Empty, TestContext.Current.CancellationToken);

        paths.AppDataDirectory.Should().Be(directory.Path);
        paths.LogsDirectory.Should().Be(Path.Combine(directory.Path, "logs"));
        paths.DefaultVaultsDirectory.Should().EndWith("Basalt");
        File.Exists(Path.Combine(directory.Path, "vaults.json")).Should().BeTrue();
        provider.GetRequiredService<IDocumentStore<AccountsDocument>>().Should().NotBeNull();
        provider.GetRequiredService<IDocumentStore<AppSettings>>().Should().NotBeNull();
        provider.GetRequiredService<IVaultConfigStore>().Should().NotBeNull();
        provider.GetRequiredService<IClock>().UtcNow.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        provider.GetRequiredService<IClock>().LocalZone.Should().NotBeNull();
        provider.GetRequiredService<IIdGenerator>().NewId().Should().NotBe(Guid.Empty);
        provider.GetRequiredService<IRandom>().Next(10).Should().BeInRange(0, 9);
        provider.GetRequiredService<IRandom>().NextDouble().Should().BeInRange(0, 1);
    }
}
