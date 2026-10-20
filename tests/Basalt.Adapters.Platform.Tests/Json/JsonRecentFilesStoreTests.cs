using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Storage;
using Basalt.Testing;
using Basalt.Testing.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Basalt.Adapters.Platform.Tests.Json;

public sealed class JsonRecentFilesStoreTests : RecentFilesStoreContract
{
    private readonly TempDirectory _directory = TempDirectory.Create("recent");
    private ServiceProvider? _services;

    protected override Task<IRecentFilesStore> CreateSutAsync()
    {
        _services = new ServiceCollection().AddBasaltPlatformAdapter(_directory.Path).BuildServiceProvider();
        return Task.FromResult(_services.GetRequiredService<IRecentFilesStore>());
    }

    protected override Task CorruptAsync() =>
        File.WriteAllTextAsync(Path.Combine(_services!.GetRequiredService<IVaultLocalDataStore>().DirectoryOf(VaultA), "recent.json"), "{ not json", Ct);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _directory.DisposeAsync();
    }
}
