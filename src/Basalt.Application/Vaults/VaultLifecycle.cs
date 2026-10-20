using Basalt.Application.Abstractions;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Storage;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Vaults;

/// <summary>Closes an open vault; succeeds when it was not open.</summary>
public sealed record CloseVault(VaultId VaultId) : ICommand<Unit>, IVaultRequest;

public sealed class CloseVaultHandler(IVaultSessions sessions, IEventBus events) : ICommandHandler<CloseVault, Unit>
{
    public async Task<Result<Unit>> HandleAsync(CloseVault command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (sessions.Get(command.VaultId).IsFailure)
        {
            return Result.Success();
        }

        await sessions.CloseAsync(command.VaultId).ConfigureAwait(false);
        await events.PublishAsync(new VaultClosed(command.VaultId), ct).ConfigureAwait(false);
        return Result.Success();
    }
}

/// <summary>
/// Removes a vault from this device's list (F-001.5): closes it, deletes its local index and sync state, and never
/// touches the vault folder or its repository.
/// </summary>
public sealed record RemoveVaultFromList(VaultId VaultId) : ICommand<Unit>, IVaultRequest;

public sealed class RemoveVaultFromListHandler(
    IVaultRegistry registry,
    IVaultSessions sessions,
    IVaultLocalDataStore localData,
    IEventBus events) : ICommandHandler<RemoveVaultFromList, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RemoveVaultFromList command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await sessions.CloseAsync(command.VaultId).ConfigureAwait(false);
        var removed = await registry.RemoveAsync(command.VaultId, ct).ConfigureAwait(false);
        if (removed.IsFailure)
        {
            return removed;
        }

        var deleted = await localData.DeleteAsync(command.VaultId, ct).ConfigureAwait(false);
        await events.PublishAsync(new VaultRemovedFromList(command.VaultId), ct).ConfigureAwait(false);
        return deleted;
    }
}

/// <summary>A registered vault and whether it is open.</summary>
public sealed record VaultListItem(VaultRegistration Registration, bool IsOpen);

/// <summary>Lists the vaults of this device, most recently opened first.</summary>
public sealed record ListVaults : IQuery<IReadOnlyList<VaultListItem>>;

public sealed class ListVaultsHandler(IVaultRegistry registry, IVaultSessions sessions) : IQueryHandler<ListVaults, IReadOnlyList<VaultListItem>>
{
    public async Task<Result<IReadOnlyList<VaultListItem>>> HandleAsync(ListVaults query, CancellationToken ct)
    {
        var vaults = await registry.GetAllAsync(ct).ConfigureAwait(false);
        return Result.Success<IReadOnlyList<VaultListItem>>([.. vaults
            .OrderByDescending(v => v.LastOpenedAt ?? v.AddedAt)
            .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(v => new VaultListItem(v, sessions.Get(v.Id).IsSuccess))]);
    }
}

/// <summary>Saves the shared settings of an open vault into <c>.basalt/vault.json</c>.</summary>
public sealed record UpdateVaultSettings(VaultId VaultId, VaultSettings Settings) : ICommand<Unit>, IVaultRequest;

public sealed class UpdateVaultSettingsHandler(IVaultSessions sessions, IVaultConfigStore configs, IEventBus events) : ICommandHandler<UpdateVaultSettings, Unit>
{
    public async Task<Result<Unit>> HandleAsync(UpdateVaultSettings command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = sessions.Get(command.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var saved = await configs.SaveVaultConfigAsync(session.Value.FileSystem, new VaultConfig(command.VaultId, command.Settings), ct).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved;
        }

        session.Value.UpdateSettings(command.Settings);
        await events.PublishAsync(new VaultSettingsChanged(command.VaultId, command.Settings), ct).ConfigureAwait(false);
        return Result.Success();
    }
}
