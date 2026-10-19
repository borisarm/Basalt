using Basalt.Application.Errors;
using Basalt.Application.Ports.Storage;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Vaults;

/// <summary>A vault known on this device.</summary>
public sealed record VaultRegistration(VaultId Id, string Name, string RootLocation, DateTimeOffset AddedAt, DateTimeOffset? LastOpenedAt = null);

/// <summary>Persisted list of vaults (<c>{AppData}/vaults.json</c>).</summary>
public sealed record VaultRegistryDocument(IReadOnlyList<VaultRegistration> Vaults)
{
    public static VaultRegistryDocument Empty { get; } = new([]);
}

/// <summary>The vaults known on this device (spec 03 §1.6). Removing a vault never touches its files.</summary>
public interface IVaultRegistry
{
    Task<IReadOnlyList<VaultRegistration>> GetAllAsync(CancellationToken ct);

    /// <returns><c>vault.already_registered</c> when the id is already present.</returns>
    Task<Result<Unit>> AddAsync(VaultRegistration registration, CancellationToken ct);

    /// <returns><c>vault.not_registered</c> when the id is unknown.</returns>
    Task<Result<Unit>> UpdateAsync(VaultRegistration registration, CancellationToken ct);

    /// <returns><c>vault.not_registered</c> when the id is unknown.</returns>
    Task<Result<Unit>> RemoveAsync(VaultId id, CancellationToken ct);
}

/// <summary><see cref="IVaultRegistry"/> over a document store; operations are serialised.</summary>
public sealed class VaultRegistry(IDocumentStore<VaultRegistryDocument> store) : IVaultRegistry, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<VaultRegistration>> GetAllAsync(CancellationToken ct) =>
        (await store.LoadAsync(ct).ConfigureAwait(false)).Value.Vaults;

    public Task<Result<Unit>> AddAsync(VaultRegistration registration, CancellationToken ct) =>
        MutateAsync(vaults =>
        {
            if (vaults.Exists(v => v.Id == registration.Id))
            {
                return new Error(ErrorCodes.VaultAlreadyRegistered, "The vault is already registered.");
            }

            vaults.Add(registration);
            return vaults;
        }, ct);

    public Task<Result<Unit>> UpdateAsync(VaultRegistration registration, CancellationToken ct) =>
        MutateAsync(vaults =>
        {
            var index = vaults.FindIndex(v => v.Id == registration.Id);
            if (index < 0)
            {
                return NotRegistered();
            }

            vaults[index] = registration;
            return vaults;
        }, ct);

    public Task<Result<Unit>> RemoveAsync(VaultId id, CancellationToken ct) =>
        MutateAsync(vaults => vaults.RemoveAll(v => v.Id == id) == 0 ? NotRegistered() : Result.Success(vaults), ct);

    private async Task<Result<Unit>> MutateAsync(Func<List<VaultRegistration>, Result<List<VaultRegistration>>> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = (await store.LoadAsync(ct).ConfigureAwait(false)).Value;
            var changed = change([.. current.Vaults]);
            return changed.IsFailure
                ? Result.Failure(changed.Error)
                : await store.SaveAsync(new VaultRegistryDocument(changed.Value), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static Error NotRegistered() => new(ErrorCodes.VaultNotRegistered, "The vault is not registered.");
}
