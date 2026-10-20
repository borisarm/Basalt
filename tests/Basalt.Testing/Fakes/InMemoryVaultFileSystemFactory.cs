using System.Collections.Concurrent;
using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;

namespace Basalt.Testing.Fakes;

/// <summary>Locations are plain strings ("/vaults/Notas"); each maps to an <see cref="InMemoryVaultFileSystem"/>.</summary>
public sealed class InMemoryVaultFileSystemFactory(TimeProvider? time = null) : IVaultFileSystemFactory
{
    private readonly ConcurrentDictionary<string, InMemoryVaultFileSystem> _volumes = new(StringComparer.Ordinal);

    public InMemoryVaultFileSystem AddFolder(string location)
    {
        var fs = new InMemoryVaultFileSystem(time: time, rootLocation: location);
        _volumes[location] = fs;
        return fs;
    }

    public void RemoveFolder(string location) => _volumes.TryRemove(location, out _);

    public InMemoryVaultFileSystem? Get(string location) => _volumes.GetValueOrDefault(location);

    public Task<Result<IVaultFileSystem>> OpenAsync(string rootLocation, CancellationToken ct) =>
        Task.FromResult(_volumes.TryGetValue(rootLocation, out var fs)
            ? Result.Success<IVaultFileSystem>(fs)
            : Result.Failure<IVaultFileSystem>(new Error(ErrorCodes.FsNotFound, "The vault folder does not exist.")));

    public async Task<Result<IVaultFileSystem>> CreateAsync(string rootLocation, CancellationToken ct)
    {
        if (_volumes.TryGetValue(rootLocation, out var existing))
        {
            await foreach (var _ in existing.EnumerateAsync(Domain.Notes.VaultPath.Root, false, ct))
            {
                return new Error(ErrorCodes.FsAlreadyExists, "The location already exists and is not an empty folder.");
            }

            return existing;
        }

        return AddFolder(rootLocation);
    }

    public string Combine(string parentLocation, string name) => parentLocation.TrimEnd('/') + "/" + name;

    public string NameOf(string location) => location.TrimEnd('/')[(location.TrimEnd('/').LastIndexOf('/') + 1)..];
}

/// <summary>Records deletions of local vault data.</summary>
public sealed class InMemoryVaultLocalDataStore : IVaultLocalDataStore
{
    public List<VaultId> Deleted { get; } = [];

    public Task<Result<Unit>> DeleteAsync(VaultId vault, CancellationToken ct)
    {
        Deleted.Add(vault);
        return Task.FromResult(Result.Success());
    }

    public string DirectoryOf(VaultId vault) => $"mem://appdata/vaults/{vault}";
}
