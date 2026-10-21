using Basalt.Application.Abstractions;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Sync;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Sync;

public sealed record VaultSyncSettingsUseCases(
    ICommandHandler<PauseSync, Unit> Pause,
    ICommandHandler<ResumeSync, Unit> Resume,
    ICommandHandler<DisableSync, Unit> Disable);

/// <summary>Per-vault sync settings: repository, triggers and enable/pause/disable (F-015.2).</summary>
public sealed partial class VaultSyncSettingsViewModel(
    ViewModelServices services,
    ISyncProfileStore profiles,
    VaultSyncSettingsUseCases useCases) : ViewModelBase(services)
{
    private VaultId _vault;

    [ObservableProperty]
    public partial bool IsConfigured { get; private set; }

    [ObservableProperty]
    public partial bool IsPaused { get; private set; }

    [ObservableProperty]
    public partial string Repository { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string TriggersSummary { get; private set; } = string.Empty;

    public void Attach(VaultId vault)
    {
        _vault = vault;
        _ = LoadAsync();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var profile = await profiles.GetAsync(_vault, CancellationToken.None).ConfigureAwait(true);
        IsConfigured = profile is not null;
        IsPaused = profile?.Paused ?? false;
        Repository = profile is null
            ? string.Empty
            : $"{profile.Repository.Owner}/{profile.Repository.Name}@{profile.Repository.Branch}";
        TriggersSummary = profile is null
            ? string.Empty
            : string.Join(", ", Triggers(profile.Triggers));
    }

    [RelayCommand]
    public async Task PauseAsync()
    {
        if (await RunAsync(ct => useCases.Pause.HandleAsync(new PauseSync(_vault), ct)).ConfigureAwait(true) is { IsSuccess: true })
        {
            IsPaused = true;
        }
    }

    [RelayCommand]
    public async Task ResumeAsync()
    {
        if (await RunAsync(ct => useCases.Resume.HandleAsync(new ResumeSync(_vault), ct)).ConfigureAwait(true) is { IsSuccess: true })
        {
            IsPaused = false;
        }
    }

    [RelayCommand]
    public async Task DisableAsync()
    {
        var disabled = await RunAsync(ct => useCases.Disable.HandleAsync(new DisableSync(_vault), ct)).ConfigureAwait(true);
        if (disabled.IsSuccess)
        {
            IsConfigured = false;
            IsPaused = false;
            Repository = string.Empty;
            TriggersSummary = string.Empty;
        }
    }

    private static IEnumerable<string> Triggers(SyncTriggers triggers)
    {
        if (triggers.OnVaultOpen)
        {
            yield return "open";
        }

        if (triggers.Interval is { } interval)
        {
            yield return $"every {interval.TotalMinutes:0}m";
        }

        if (triggers.AfterEditIdle is not null)
        {
            yield return "after edit";
        }

        if (triggers.OnAppBackground)
        {
            yield return "background";
        }
    }
}
