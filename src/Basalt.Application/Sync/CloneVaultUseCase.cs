using System.Text.Json;
using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Vaults;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Sync;

/// <summary>Creates a local vault linked to an existing repository; the first sync downloads it (spec 03 §2 and 06 §10).</summary>
public sealed record CloneVaultFromRepository(
    GitHubAccountId AccountId,
    RemoteRepositoryRef Repository,
    string LocalParent,
    string VaultName,
    SyncScope? Scope = null,
    SyncPolicy? Policy = null,
    SyncTriggers? Triggers = null,
    CommitSettings? Commit = null) : ICommand<VaultRegistration>;

public sealed class CloneVaultFromRepositoryHandler(
    IVaultFileSystemFactory fileSystems,
    IVaultConfigStore configs,
    IVaultRegistry registry,
    ISyncProfileStore profiles,
    IRemoteRepositoryFactory repositories,
    IIdGenerator ids,
    IClock clock) : ICommandHandler<CloneVaultFromRepository, VaultRegistration>
{
    private static readonly VaultPath ConfigPath = VaultPath.Create(".basalt/vault.json").Value;

    public async Task<Result<VaultRegistration>> HandleAsync(CloneVaultFromRepository command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var name = VaultName.Create(command.VaultName);
        if (name.IsFailure)
        {
            return name.Error;
        }

        var repository = repositories.Create(command.AccountId, command.Repository);
        var head = await repository.GetHeadAsync(null, ct).ConfigureAwait(false);
        if (head.IsFailure)
        {
            return head.Error;
        }

        if (head.Value.Head is not { } remoteHead)
        {
            return new Error(RemoteErrors.RepositoryEmpty, "The repository has no commits to clone.");
        }

        var id = await RemoteIdAsync(repository, remoteHead, ct).ConfigureAwait(false);
        if (id.IsFailure)
        {
            return id.Error;
        }

        var location = fileSystems.Combine(command.LocalParent, name.Value.Value);
        var fs = await fileSystems.CreateAsync(location, ct).ConfigureAwait(false);
        if (fs.IsFailure)
        {
            return fs.Error.Code == ErrorCodes.FsAlreadyExists
                ? new Error(ErrorCodes.VaultLocationExists, "A non-empty folder with that name already exists.").With("location", location)
                : fs.Error;
        }

        var vaultId = id.Value;
        var saved = await configs.SaveVaultConfigAsync(fs.Value, new VaultConfig(vaultId, VaultSettings.Default), ct).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        var registration = new VaultRegistration(vaultId, name.Value.Value, location, clock.UtcNow);
        var added = await registry.AddAsync(registration, ct).ConfigureAwait(false);
        if (added.IsFailure)
        {
            return added.Error;
        }

        var profile = new SyncProfile(
            vaultId,
            command.AccountId,
            command.Repository,
            Enabled: true,
            Paused: false,
            command.Triggers ?? new SyncTriggers(),
            command.Policy ?? SyncPolicy.Default,
            command.Scope ?? SyncScope.Default,
            command.Commit ?? new CommitSettings());
        var profileSaved = await profiles.SaveAsync(profile, ct).ConfigureAwait(false);
        if (profileSaved.IsFailure)
        {
            await registry.RemoveAsync(vaultId, ct).ConfigureAwait(false);
            return profileSaved.Error;
        }

        return registration;
    }

    private async Task<Result<VaultId>> RemoteIdAsync(
        IRemoteRepository repository,
        RemoteHead head,
        CancellationToken ct)
    {
        var tree = await repository.GetTreeAsync(head.Tree, ct).ConfigureAwait(false);
        if (tree.IsFailure)
        {
            return tree.Error;
        }

        var config = tree.Value.Files.FirstOrDefault(file => file.Path == ConfigPath);
        if (config is null)
        {
            return new VaultId(ids.NewId());
        }

        var blob = await repository.GetBlobAsync(config.Sha, ct).ConfigureAwait(false);
        if (blob.IsFailure)
        {
            return blob.Error;
        }

        try
        {
            using var document = JsonDocument.Parse(blob.Value);
            return document.RootElement.TryGetProperty("vaultId", out var id)
                && VaultId.Parse(id.GetString() ?? string.Empty) is { IsSuccess: true } parsed
                    ? parsed.Value
                    : new VaultId(ids.NewId());
        }
        catch (JsonException)
        {
            return new VaultId(ids.NewId());
        }
    }
}
