using Basalt.Application.Errors;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Sync;
using Basalt.Application.Vaults;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.Application.Tests.Sync;

public sealed class SyncLinkUseCaseTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly VaultId Vault = new(Guid.NewGuid());
    private static readonly GitHubAccountId Account = new(Guid.NewGuid());
    private static readonly RemoteRepositoryRef Ref = new(GitHubHost.GitHubDotCom, "acme", "notes", "main");
    private static readonly RemoteRepositoryInfo Info = new("acme", "notes", "main", true, false, true);

    private readonly InMemoryVaultFileSystem _files = new();
    private readonly VaultSessions _sessions = new();
    private readonly InMemoryRepositoryAdministration _admin = new(Info);
    private readonly InMemoryRemoteRepositoryFactory _repositories = new();
    private readonly InMemorySyncProfileStore _profiles = new();

    public async ValueTask InitializeAsync()
    {
        var session = new VaultSession(Vault, "Notes", _files, VaultSettings.Default);
        _sessions.Add(session);
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _sessions.DisposeAsync();

    [Fact]
    public async Task Preview_UploadsANonEmptyLocalVault()
    {
        await _files.WriteAllBytesAsync(VaultPath.Create("A.md").Value, "a"u8.ToArray(), null, Ct);
        var handler = new PreviewSyncLinkHandler(_sessions, _admin, _repositories);

        var result = await handler.HandleAsync(new PreviewSyncLink(Vault, Account, Ref, SyncScope.Default, SyncPolicy.Default), Ct);

        result.Value.Mode.Should().Be(SyncLinkMode.Upload);
        result.Value.LocalOnly.Should().Be(1);
        result.Value.IsPublic.Should().BeFalse();
    }

    [Fact]
    public async Task Preview_EmptySides_IsInit()
    {
        var handler = new PreviewSyncLinkHandler(_sessions, _admin, _repositories);

        var result = await handler.HandleAsync(new PreviewSyncLink(Vault, Account, Ref, SyncScope.Default, SyncPolicy.Default), Ct);

        result.Value.Mode.Should().Be(SyncLinkMode.Init);
    }

    [Fact]
    public async Task Configure_MergeRequiresConfirmation()
    {
        await _files.WriteAllBytesAsync(VaultPath.Create("A.md").Value, "a"u8.ToArray(), null, Ct);
        await _repositories.Create(Account, Ref).CreateInitialCommitAsync(
            VaultPath.Create("B.md").Value, "b"u8.ToArray(), "remote", Ct);
        await using var coordinator = new SyncCoordinator(new NoRunnerFactory(), new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        var handler = new ConfigureSyncHandler(_sessions, _profiles, _admin, _repositories, coordinator);

        var result = await handler.HandleAsync(new ConfigureSync(Vault, Account, Ref), Ct);

        result.Error.Code.Should().Be(SyncErrors.ConfirmationRequired);
        (await _profiles.GetAsync(Vault, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Configure_UploadsAndAttachesTheRepository()
    {
        await _files.WriteAllBytesAsync(VaultPath.Create("A.md").Value, "a"u8.ToArray(), null, Ct);
        await using var coordinator = new SyncCoordinator(new NoRunnerFactory(), new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        var handler = new ConfigureSyncHandler(_sessions, _profiles, _admin, _repositories, coordinator);

        var result = await handler.HandleAsync(
            new ConfigureSync(Vault, Account, Ref, Triggers: new SyncTriggers(OnVaultOpen: false)), Ct);

        result.IsSuccess.Should().BeTrue();
        (await _profiles.GetAsync(Vault, Ct)).Should().NotBeNull();
        _sessions.Get(Vault).Value.TryGet<IRemoteRepository>().Should().NotBeNull();
    }

    [Fact]
    public async Task Preview_ArchivedRepository_IsForbidden()
    {
        var handler = new PreviewSyncLinkHandler(_sessions, new InMemoryRepositoryAdministration(Info with { IsArchived = true }), _repositories);

        var result = await handler.HandleAsync(new PreviewSyncLink(Vault, Account, Ref, SyncScope.Default, SyncPolicy.Default), Ct);

        result.Error.Code.Should().Be(RemoteErrors.Forbidden);
    }

    [Fact]
    public async Task Preview_WithoutPushPermission_IsForbidden()
    {
        var handler = new PreviewSyncLinkHandler(_sessions, new InMemoryRepositoryAdministration(Info with { CanPush = false }), _repositories);

        var result = await handler.HandleAsync(new PreviewSyncLink(Vault, Account, Ref, SyncScope.Default, SyncPolicy.Default), Ct);

        result.Error.Code.Should().Be(RemoteErrors.Forbidden);
    }

    [Fact]
    public async Task Configure_AcceptsTheMergeWhenConfirmed()
    {
        await _files.WriteAllBytesAsync(VaultPath.Create("A.md").Value, "a"u8.ToArray(), null, Ct);
        await _repositories.Create(Account, Ref).CreateInitialCommitAsync(
            VaultPath.Create("B.md").Value, "b"u8.ToArray(), "remote", Ct);
        await using var coordinator = new SyncCoordinator(new NoRunnerFactory(), new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        var handler = new ConfigureSyncHandler(_sessions, _profiles, _admin, _repositories, coordinator);

        var result = await handler.HandleAsync(
            new ConfigureSync(Vault, Account, Ref, Triggers: new SyncTriggers(OnVaultOpen: false), AcceptMerge: true), Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Mode.Should().Be(SyncLinkMode.Merge);
    }

    [Fact]
    public async Task Preview_RemoteHeadFailure_Propagates()
    {
        var repository = Substitute.For<IRemoteRepository>();
        repository.GetHeadAsync(null, Arg.Any<CancellationToken>())
            .Returns(Result<RemoteHeadCheck>.Failure(new Error(RemoteErrors.Network, "offline")));
        var factory = Substitute.For<IRemoteRepositoryFactory>();
        factory.Create(Account, Ref).Returns(repository);
        var handler = new PreviewSyncLinkHandler(_sessions, _admin, factory);

        var result = await handler.HandleAsync(new PreviewSyncLink(Vault, Account, Ref, SyncScope.Default, SyncPolicy.Default), Ct);

        result.Error.Code.Should().Be(RemoteErrors.Network);
    }

    [Fact]
    public async Task Preview_UnknownVault_ReturnsNotOpen()
    {
        var handler = new PreviewSyncLinkHandler(_sessions, _admin, _repositories);

        var result = await handler.HandleAsync(new PreviewSyncLink(new VaultId(Guid.NewGuid()), Account, Ref, SyncScope.Default, SyncPolicy.Default), Ct);

        result.Error.Code.Should().Be(Basalt.Application.Errors.ErrorCodes.VaultNotOpen);
    }
    [Fact]
    public async Task Configure_UnknownVault_ReturnsNotOpen()
    {
        await using var coordinator = new SyncCoordinator(new NoRunnerFactory(), new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        var handler = new ConfigureSyncHandler(_sessions, _profiles, _admin, _repositories, coordinator);

        var result = await handler.HandleAsync(new ConfigureSync(new VaultId(Guid.NewGuid()), Account, Ref), Ct);

        result.Error.Code.Should().Be(Basalt.Application.Errors.ErrorCodes.VaultNotOpen);
    }
    [Fact]
    public async Task Preview_EqualAndConflictingFiles_AreCounted()
    {
        var path = VaultPath.Create("A.md").Value;
        await _files.WriteAllBytesAsync(path, "local"u8.ToArray(), null, Ct);
        await _repositories.Create(Account, Ref).CreateInitialCommitAsync(path, "remote"u8.ToArray(), "remote", Ct);
        var handler = new PreviewSyncLinkHandler(_sessions, _admin, _repositories);

        var result = await handler.HandleAsync(new PreviewSyncLink(Vault, Account, Ref, SyncScope.Default, SyncPolicy.Default), Ct);

        result.Value.Mode.Should().Be(SyncLinkMode.Merge);
        result.Value.Conflicts.Should().Be(1);
    }

    [Fact]
    public async Task Preview_RemoteFileOnly_IsDownload()
    {
        await _repositories.Create(Account, Ref).CreateInitialCommitAsync(
            VaultPath.Create("A.md").Value, "remote"u8.ToArray(), "remote", Ct);
        var handler = new PreviewSyncLinkHandler(_sessions, _admin, _repositories);

        var result = await handler.HandleAsync(new PreviewSyncLink(Vault, Account, Ref, SyncScope.Default, SyncPolicy.Default), Ct);

        result.Value.Mode.Should().Be(SyncLinkMode.Download);
        result.Value.RemoteOnly.Should().Be(1);
    }
    private sealed class NoRunnerFactory : IVaultSyncRunnerFactory
    {
        public IVaultSyncRunner Create(SyncProfile profile) =>
            throw new InvalidOperationException("No automatic run expected.");
    }
    [Fact]
    public async Task Configure_UsesDefaultOptions()
    {
        await _files.WriteAllBytesAsync(VaultPath.Create("A.md").Value, "a"u8.ToArray(), null, Ct);
        await using var coordinator = new SyncCoordinator(new NoRunnerFactory(), new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        var handler = new ConfigureSyncHandler(_sessions, _profiles, _admin, _repositories, coordinator);

        var result = await handler.HandleAsync(new ConfigureSync(Vault, Account, Ref, Triggers: new SyncTriggers(OnVaultOpen: false)), Ct);
        var profile = (await _profiles.GetAsync(Vault, Ct))!;

        result.IsSuccess.Should().BeTrue();
        profile.Scope.Should().Be(SyncScope.Default);
        profile.Policy.Should().Be(SyncPolicy.Default);
        profile.Commit.Should().Be(new CommitSettings());
        profile.Enabled.Should().BeTrue();
        profile.Paused.Should().BeFalse();
    }

    [Fact]
    public async Task Configure_WhenSavingFails_ReturnsTheErrorAndDoesNotAttach()
    {
        await _files.WriteAllBytesAsync(VaultPath.Create("A.md").Value, "a"u8.ToArray(), null, Ct);
        var failure = new Error(ErrorCodes.StorageIo, "disk");
        var profiles = Substitute.For<ISyncProfileStore>();
        profiles.SaveAsync(Arg.Any<SyncProfile>(), Arg.Any<CancellationToken>()).Returns(Result<Unit>.Failure(failure));
        await using var coordinator = new SyncCoordinator(new NoRunnerFactory(), new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        var handler = new ConfigureSyncHandler(_sessions, profiles, _admin, _repositories, coordinator);

        var result = await handler.HandleAsync(new ConfigureSync(Vault, Account, Ref, Triggers: new SyncTriggers(OnVaultOpen: false)), Ct);

        result.Error.Should().Be(failure);
        _sessions.Get(Vault).Value.TryGet<IRemoteRepository>().Should().BeNull();
    }

    [Fact]
    public async Task Preview_ReportsWhyTheRepositoryIsRefused()
    {
        var archived = await new PreviewSyncLinkHandler(_sessions, new InMemoryRepositoryAdministration(Info with { IsArchived = true }), _repositories)
            .HandleAsync(new PreviewSyncLink(Vault, Account, Ref, SyncScope.Default, SyncPolicy.Default), Ct);
        var readOnly = await new PreviewSyncLinkHandler(_sessions, new InMemoryRepositoryAdministration(Info with { CanPush = false }), _repositories)
            .HandleAsync(new PreviewSyncLink(Vault, Account, Ref, SyncScope.Default, SyncPolicy.Default), Ct);

        archived.Error.Message.Should().Be("The repository is archived.");
        readOnly.Error.Message.Should().Be("The account cannot push to this repository.");
    }

    [Fact]
    public async Task SyncLinkHandlers_RejectNulls()
    {
        await using var coordinator = new SyncCoordinator(new NoRunnerFactory(), new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        await FluentActions.Awaiting(() => new PreviewSyncLinkHandler(_sessions, _admin, _repositories).HandleAsync(null!, Ct))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => new ConfigureSyncHandler(_sessions, _profiles, _admin, _repositories, coordinator).HandleAsync(null!, Ct))
            .Should().ThrowAsync<ArgumentNullException>();
    }
}
