using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Storage;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Vaults;

/// <summary>Creates a new vault folder with <c>.basalt/vault.json</c> and registers it (F-001.1).</summary>
/// <returns><c>vault.invalid_name</c>, <c>vault.location_exists</c> or storage errors.</returns>
public sealed record CreateVault(string Name, string ParentLocation) : ICommand<VaultRegistration>;

public sealed class CreateVaultHandler(
    IVaultFileSystemFactory fileSystems,
    IVaultConfigStore configs,
    IVaultRegistry registry,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<CreateVault, VaultRegistration>
{
    public async Task<Result<VaultRegistration>> HandleAsync(CreateVault command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var name = VaultName.Create(command.Name);
        if (name.IsFailure)
        {
            return name.Error;
        }

        var location = fileSystems.Combine(command.ParentLocation, name.Value.Value);
        var fs = await fileSystems.CreateAsync(location, ct).ConfigureAwait(false);
        if (fs.IsFailure)
        {
            return fs.Error.Code == ErrorCodes.FsAlreadyExists
                ? new Error(ErrorCodes.VaultLocationExists, "A non-empty folder with that name already exists.").With("location", location)
                : fs.Error;
        }

        var id = new VaultId(ids.NewId());
        var saved = await configs.SaveVaultConfigAsync(fs.Value, new VaultConfig(id, VaultSettings.Default), ct).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        var registration = new VaultRegistration(id, name.Value.Value, location, clock.UtcNow);
        var added = await registry.AddAsync(registration, ct).ConfigureAwait(false);
        return added.IsFailure ? added.Error : registration;
    }
}
