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

/// <summary>How the status pill is coloured (F-015.4): quiet, synced, working or waiting, needs attention, failed.</summary>
public enum SyncTone
{
    Neutral,
    Success,
    Accent,
    Warning,
    Danger,
}

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

    [ObservableProperty]
    public partial int PendingChanges { get; private set; }

    [ObservableProperty]
    public partial int Conflicts { get; private set; }

    [ObservableProperty]
    public partial int Quarantined { get; private set; }

    public bool IsConfigured => State != SyncState.NotConfigured;

    public bool IsPaused => State == SyncState.Paused || State == SyncState.Disabled;

    public bool HasConflicts => Conflicts > 0;

    /// <summary>Status bar text (F-015.4): state and, when synced, how long ago.</summary>
    public string Summary => State switch
    {
        SyncState.NotConfigured => Localizer.Get("Sync.State.NotConfigured"),
        SyncState.Disabled => Localizer.Get("Sync.State.Disabled"),
        SyncState.Paused => Localizer.Get("Sync.State.Paused"),
        SyncState.Syncing => Localizer.Get("Sync.State.Syncing"),
        SyncState.Error => Localizer.Get("Sync.State.Error"),
        _ when PendingChanges > 0 => Localizer.Format("Sync.ChangesWaiting", PendingChanges),
        _ when LastFinished is { } finished => Localizer.Format("Sync.SyncedAgo", Relative(finished)),
        _ => Localizer.Get("Sync.State.UpToDate"),
    };

    /// <summary>The colour of the status pill, following <see cref="Summary"/>.</summary>
    public SyncTone Tone => State switch
    {
        SyncState.NotConfigured or SyncState.Disabled or SyncState.Paused => SyncTone.Neutral,
        SyncState.Syncing => SyncTone.Accent,
        SyncState.Error => SyncTone.Danger,
        _ when PendingChanges > 0 => SyncTone.Accent,
        _ => SyncTone.Success,
    };

    /// <summary>Conflict count shown next to the status (F-015.4).</summary>
    public string ConflictSummary => Localizer.Format("Sync.Conflicts", Conflicts);

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
        PendingChanges = status.PendingChanges;
        Conflicts = status.Conflicts;
        Quarantined = status.Quarantined;
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Tone));
        OnPropertyChanged(nameof(ConflictSummary));
    }

    private string Relative(DateTimeOffset finished)
    {
        var elapsed = Services.Time.GetUtcNow() - finished;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return Localizer.Get("Sync.JustNow");
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return Localizer.Format("Sync.MinutesAgo", (int)elapsed.TotalMinutes);
        }

        return elapsed < TimeSpan.FromDays(1)
            ? Localizer.Format("Sync.HoursAgo", (int)elapsed.TotalHours)
            : Localizer.Format("Sync.DaysAgo", (int)elapsed.TotalDays);
    }
}
