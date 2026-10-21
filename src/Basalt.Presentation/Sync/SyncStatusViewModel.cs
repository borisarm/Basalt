using Basalt.Application.Abstractions;
using Basalt.Application.Sync;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Sync;

public sealed record SyncStatusUseCases(
    IQueryHandler<GetSyncStatus, SyncStatusView> Status,
    ICommandHandler<SyncVault, SyncCycleResult?> Sync,
    ICommandHandler<PauseSync, Unit> Pause,
    ICommandHandler<ResumeSync, Unit> Resume);

/// <summary>Sync state, manual trigger, pause/resume (F-015.1, F-015.2).</summary>
public sealed partial class SyncStatusViewModel(ViewModelServices services, SyncStatusUseCases useCases)
    : ViewModelBase(services)
{
    private VaultId? _vault;

    [ObservableProperty]
    public partial SyncState State { get; private set; } = SyncState.NotConfigured;

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial Error? LastError { get; private set; }

    [ObservableProperty]
    public partial int ConsecutiveFailures { get; private set; }

    [ObservableProperty]
    public partial DateTimeOffset? LastFinished { get; private set; }

    [ObservableProperty]
    public partial DateTimeOffset? NextRetryAt { get; private set; }

    public bool IsConfigured => State != SyncState.NotConfigured;

    public bool IsPaused => State == SyncState.Paused || State == SyncState.Disabled;

    public void Attach(VaultId vault)
    {
        _vault = vault;
        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_vault is not { } vault)
        {
            return;
        }

        var status = await RunAsync(ct => useCases.Status.HandleAsync(new GetSyncStatus(vault), ct)).ConfigureAwait(true);
        if (status.IsFailure)
        {
            return;
        }

        Apply(status.Value);
    }

    [RelayCommand]
    public async Task SyncNowAsync()
    {
        if (_vault is not { } vault)
        {
            return;
        }

        var result = await RunAsync(ct => useCases.Sync.HandleAsync(new SyncVault(vault), ct)).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        await RefreshAsync().ConfigureAwait(true);
        Services.Notifications.Show(new Notification(
            NotificationSeverity.Success, Localizer.Get("Sync.Completed")));
    }

    [RelayCommand]
    public async Task PauseAsync()
    {
        if (_vault is { } vault)
        {
            await RunAsync(ct => useCases.Pause.HandleAsync(new PauseSync(vault), ct)).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public async Task ResumeAsync()
    {
        if (_vault is { } vault)
        {
            await RunAsync(ct => useCases.Resume.HandleAsync(new ResumeSync(vault), ct)).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private void Apply(SyncStatusView status)
    {
        State = status.State;
        IsRunning = status.IsRunning;
        LastError = status.LastError;
        ConsecutiveFailures = status.ConsecutiveFailures;
        LastFinished = status.LastFinished;
        NextRetryAt = status.NextRetryAt;
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(IsPaused));
    }
}
