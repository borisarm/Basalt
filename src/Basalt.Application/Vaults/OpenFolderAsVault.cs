using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Storage;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Vaults;

/// <summary>
/// Registers an existing folder as a vault (F-001.3/F-001.4): keeps the <c>VaultId</c> of an existing
/// <c>.basalt/vault.json</c> (a vault from another device) or creates one. An unreadable vault.json is never
/// overwritten (<c>vault.config_invalid</c>).
/// </summary>
public sealed record OpenFolderAsVault(string Location) : ICommand<VaultRegistration>;

public sealed class OpenFolderAsVaultHandler(
    IVaultFileSystemFactory fileSystems,
    IVaultConfigStore configs,
    IVaultRegistry registry,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<OpenFolderAsVault, VaultRegistration>
{
    public async Task<Result<VaultRegistration>> HandleAsync(OpenFolderAsVault command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var fs = await fileSystems.OpenAsync(command.Location, ct).ConfigureAwait(false);
        if (fs.IsFailure)
        {
            return fs.Error.Code == ErrorCodes.FsNotFound ? LocationMissing(command.Location) : fs.Error;
        }

        var config = await configs.LoadVaultConfigAsync(fs.Value, ct).ConfigureAwait(false);
        VaultId id;
        switch (config.Status)
        {
            case DocumentLoadStatus.Invalid:
                return new Error(ErrorCodes.VaultConfigInvalid, "The vault configuration file cannot be read.").With("problem", config.Problem);
            case DocumentLoadStatus.Missing:
                id = new VaultId(ids.NewId());
                var saved = await configs.SaveVaultConfigAsync(fs.Value, new VaultConfig(id, VaultSettings.Default), ct).ConfigureAwait(false);
                if (saved.IsFailure)
                {
                    return saved.Error;
                }

                break;
            default:
                id = config.Value!.Id;
                break;
        }

        var existing = (await registry.GetAllAsync(ct).ConfigureAwait(false)).FirstOrDefault(r => r.Id == id);
        if (existing is not null)
        {
            if (existing.RootLocation == command.Location)
            {
                return existing;
            }

            // The folder moved: keep the identity, update the location.
            var moved = existing with { RootLocation = command.Location };
            var updated = await registry.UpdateAsync(moved, ct).ConfigureAwait(false);
            return updated.IsFailure ? updated.Error : moved;
        }

        var name = VaultName.Create(fileSystems.NameOf(command.Location));
        var registration = new VaultRegistration(id, name.IsSuccess ? name.Value.Value : "Vault", command.Location, clock.UtcNow);
        var added = await registry.AddAsync(registration, ct).ConfigureAwait(false);
        return added.IsFailure ? added.Error : registration;
    }

    internal static Error LocationMissing(string location) =>
        new Error(ErrorCodes.VaultLocationMissing, "The vault folder no longer exists.").With("location", location);
}
