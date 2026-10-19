using System.Collections.Concurrent;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Storage;
using Basalt.Domain.Common;

namespace Basalt.Testing.Fakes;

/// <summary>In-memory <see cref="IVaultConfigStore"/> keyed by vault root location.</summary>
public sealed class InMemoryVaultConfigStore : IVaultConfigStore
{
    private readonly ConcurrentDictionary<string, VaultConfig> _configs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _corrupt = new(StringComparer.Ordinal);

    public void Corrupt(IVaultFileSystem vault) => _corrupt[vault.RootLocation] = true;

    public Task<DocumentLoad<VaultConfig?>> LoadVaultConfigAsync(IVaultFileSystem vault, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vault);
        if (_corrupt.ContainsKey(vault.RootLocation))
        {
            return Task.FromResult(new DocumentLoad<VaultConfig?>(null, DocumentLoadStatus.Invalid, "corrupt"));
        }

        return Task.FromResult(_configs.TryGetValue(vault.RootLocation, out var config)
            ? new DocumentLoad<VaultConfig?>(config, DocumentLoadStatus.Loaded)
            : new DocumentLoad<VaultConfig?>(null, DocumentLoadStatus.Missing));
    }

    public Task<Result<Unit>> SaveVaultConfigAsync(IVaultFileSystem vault, VaultConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vault);
        _configs[vault.RootLocation] = config;
        _corrupt.TryRemove(vault.RootLocation, out _);
        return Task.FromResult(Result.Success());
    }
}
