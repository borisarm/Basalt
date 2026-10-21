using Basalt.Application.Abstractions;
using Basalt.Application.Sync;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Sync;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.Presentation.Tests.Sync;

public sealed class SyncStatusViewModelTests
{
    private static readonly VaultId Vault = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly IQueryHandler<GetSyncStatus, SyncStatusView> _status =
        Substitute.For<IQueryHandler<GetSyncStatus, SyncStatusView>>();
    private readonly ICommandHandler<SyncVault, SyncCycleResult?> _sync =
        Substitute.For<ICommandHandler<SyncVault, SyncCycleResult?>>();
    private readonly ICommandHandler<PauseSync, Unit> _pause = Substitute.For<ICommandHandler<PauseSync, Unit>>();
    private readonly ICommandHandler<ResumeSync, Unit> _resume = Substitute.For<ICommandHandler<ResumeSync, Unit>>();
    private readonly FakeNotificationService _notifications = new();
    private readonly SyncStatusViewModel _vm;

    public SyncStatusViewModelTests()
    {
        _status.HandleAsync(Arg.Any<GetSyncStatus>(), Arg.Any<CancellationToken>())
            .Returns(Result<SyncStatusView>.Success(new SyncStatusView(
                Vault, SyncState.UpToDate, false, SyncCycleStatus.UpToDate, null, Now, null, 0)));
        _sync.HandleAsync(Arg.Any<SyncVault>(), Arg.Any<CancellationToken>())
            .Returns(Result<SyncCycleResult?>.Success(null));
        _pause.HandleAsync(Arg.Any<PauseSync>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _resume.HandleAsync(Arg.Any<ResumeSync>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _vm = new SyncStatusViewModel(
            new ViewModelServices(new ImmediateDispatcher(), _notifications, PresentationFakes.Localizer(), new FakeTimeProvider(Now)),
            new SyncStatusUseCases(_status, _sync, _pause, _resume));
    }

    [Fact]
    public async Task Attach_LoadsTheCurrentStatus()
    {
        _vm.Attach(Vault);
        await _vm.RefreshAsync();

        _vm.State.Should().Be(SyncState.UpToDate);
        _vm.LastFinished.Should().Be(Now);
        _vm.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task SyncNow_RunsACycleAndRefreshes()
    {
        _vm.Attach(Vault);

        await _vm.SyncNowAsync();

        await _sync.Received(1).HandleAsync(Arg.Any<SyncVault>(), Arg.Any<CancellationToken>());
        _notifications.Shown.Should().ContainSingle();
    }

    [Fact]
    public async Task Pause_InvokesTheUseCaseAndReloads()
    {
        _vm.Attach(Vault);

        await _vm.PauseAsync();

        await _pause.Received(1).HandleAsync(Arg.Any<PauseSync>(), Arg.Any<CancellationToken>());
    }
}
