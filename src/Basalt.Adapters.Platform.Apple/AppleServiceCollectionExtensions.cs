using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Basalt.Adapters.Platform.Apple;

/// <summary>DI registration of the Apple platform adapter (Keychain, paths, connectivity, spec 12 §3).</summary>
public static class AppleServiceCollectionExtensions
{
    /// <param name="appDataOverride">Alternative AppData folder (tests, portable installs).</param>
    public static IServiceCollection AddBasaltApplePlatformAdapter(this IServiceCollection services, string? appDataOverride = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton<IPlatformPaths>(_ => new ApplePlatformPaths(appDataOverride)));
        services.Replace(ServiceDescriptor.Singleton<ISecretStore, AppleKeychainStore>());
        services.Replace(ServiceDescriptor.Singleton<IConnectivityMonitor, AppleConnectivityMonitor>());
        return services;
    }
}
