using Basalt.Application.Ports.FileSystem;
using Microsoft.Extensions.DependencyInjection;

namespace Basalt.Adapters.FileSystem;

/// <summary>DI registration of the System.IO file system adapter.</summary>
public static class FileSystemServiceCollectionExtensions
{
    public static IServiceCollection AddBasaltFileSystemAdapter(this IServiceCollection services)
    {
        services.AddSingleton<IVaultFileSystemFactory, PhysicalVaultFileSystemFactory>();
        return services;
    }
}
