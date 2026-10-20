using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Storage;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Vaults;

/// <summary>Summary of an open vault. <see cref="ConfigWarning"/> is set when vault.json could not be read.</summary>
public sealed record VaultSessionInfo(VaultId Id, string Name, string RootLocation, VaultSettings Settings, string? ConfigWarning);

/// <summary>Opens a registered vault (spec 03 §2): creates its session and runs the session initializers.</summary>
/// <returns><c>vault.not_registered</c>, <c>vault.location_missing</c> (F-001.6) or initializer errors.</returns>
public sealed record OpenVault(VaultId VaultId) : ICommand<VaultSessionInfo>, IVaultRequest;

public sealed class OpenVaultHandler(
    IVaultRegistry registry,
    IVaultFileSystemFactory fileSystems,
    IVaultConfigStore configs,
    IVaultSessions sessions,
    IEnumerable<IVaultSessionInitializer> initializers,
    IEventBus events,
    IClock clock) : ICommandHandler<OpenVault, VaultSessionInfo>
{
    public async Task<Result<VaultSessionInfo>> HandleAsync(OpenVault command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var registration = (await registry.GetAllAsync(ct).ConfigureAwait(false)).FirstOrDefault(r => r.Id == command.VaultId);
        if (registration is null)
        {
            return new Error(ErrorCodes.VaultNotRegistered, "The vault is not registered.");
        }

        if (sessions.Get(command.VaultId) is { IsSuccess: true } open)
        {
            return Info(open.Value, null);
        }

        var fs = await fileSystems.OpenAsync(registration.RootLocation, ct).ConfigureAwait(false);
        if (fs.IsFailure)
        {
            return fs.Error.Code == ErrorCodes.FsNotFound ? OpenFolderAsVaultHandler.LocationMissing(registration.RootLocation) : fs.Error;
        }

        var config = await configs.LoadVaultConfigAsync(fs.Value, ct).ConfigureAwait(false);
        string? warning = null;
        var settings = VaultSettings.Default;
        switch (config.Status)
        {
            case DocumentLoadStatus.Loaded:
                settings = config.Value!.Settings;
                break;
            case DocumentLoadStatus.Missing:
                // vault.json was deleted (or never synced): recreate it with the registered identity.
                await configs.SaveVaultConfigAsync(fs.Value, new VaultConfig(registration.Id, settings), ct).ConfigureAwait(false);
                break;
            default:
                // Unreadable (e.g. sync conflict markers): open with defaults and never overwrite it (spec 08 §1.1).
                warning = config.Problem ?? "invalid";
                break;
        }

        var session = new VaultSession(registration.Id, registration.Name, fs.Value, settings);
        foreach (var initializer in initializers)
        {
            var initialized = await initializer.InitializeAsync(session, ct).ConfigureAwait(false);
            if (initialized.IsFailure)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return initialized.Error;
            }
        }

        var added = sessions.Add(session);
        if (added.IsFailure)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return Info(sessions.Get(command.VaultId).Value, warning);
        }

        await registry.UpdateAsync(registration with { LastOpenedAt = clock.UtcNow }, ct).ConfigureAwait(false);
        await events.PublishAsync(new VaultOpened(registration.Id), ct).ConfigureAwait(false);
        return Info(session, warning);
    }

    private static VaultSessionInfo Info(VaultSession session, string? warning) =>
        new(session.Id, session.Name, session.FileSystem.RootLocation, session.Settings, warning);
}
