using Basalt.Application.Ports.FileSystem;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Ports.Storage;

/// <summary>Identity and shared settings of a vault, stored in <c>.basalt/vault.json</c> (synchronised).</summary>
public sealed record VaultConfig(VaultId Id, VaultSettings Settings);

/// <summary>Typed access to the JSON files inside a vault's <c>.basalt/</c> folder (spec 03 §1.6, 08 §1).</summary>
public interface IVaultConfigStore
{
    /// <summary>Loads <c>.basalt/vault.json</c>; <see cref="DocumentLoadStatus.Missing"/>/<see cref="DocumentLoadStatus.Invalid"/>
    /// return a null value.</summary>
    Task<DocumentLoad<VaultConfig?>> LoadVaultConfigAsync(IVaultFileSystem vault, CancellationToken ct);

    Task<Result<Unit>> SaveVaultConfigAsync(IVaultFileSystem vault, VaultConfig config, CancellationToken ct);
}
