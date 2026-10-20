using Basalt.Adapters.Platform.Json;
using Basalt.Application.Accounts;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Settings;
using Basalt.Application.Vaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Basalt.Adapters.Platform;

/// <summary>DI registration of the portable platform adapter (JSON stores under <c>{AppData}</c>).</summary>
public static class PlatformServiceCollectionExtensions
{
    /// <param name="appDataOverride">Alternative AppData folder (tests, portable installs).</param>
    public static IServiceCollection AddBasaltPlatformAdapter(this IServiceCollection services, string? appDataOverride = null)
    {
        services.TryAddSingleton<IPlatformPaths>(_ => new DesktopPlatformPaths(appDataOverride));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IIdGenerator, GuidIdGenerator>();
        services.TryAddSingleton<IRandom, SystemRandom>();
        services.AddSingleton<IDocumentStore<VaultRegistryDocument>>(sp => Store(sp, "vaults.json", PlatformJsonContext.Default.VaultRegistryDocument, () => VaultRegistryDocument.Empty));
        services.AddSingleton<IDocumentStore<AccountsDocument>>(sp => Store(sp, "accounts.json", PlatformJsonContext.Default.AccountsDocument, () => AccountsDocument.Empty));
        services.AddSingleton<IDocumentStore<AppSettings>>(sp => Store(sp, "settings.json", PlatformJsonContext.Default.AppSettings, () => AppSettings.Default));
        services.AddSingleton<IVaultConfigStore, JsonVaultConfigStore>();
        services.AddSingleton<Application.Ports.FileSystem.IVaultLocalDataStore, VaultLocalDataStore>();
        return services;
    }

    private static JsonFileStore<T> Store<T>(IServiceProvider services, string fileName, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, Func<T> defaultValue)
        where T : class =>
        new(
            Path.Combine(services.GetRequiredService<IPlatformPaths>().AppDataDirectory, fileName),
            typeInfo,
            defaultValue,
            currentVersion: 1,
            migrations: [],
            indented: false,
            services.GetRequiredService<TimeProvider>());
}
