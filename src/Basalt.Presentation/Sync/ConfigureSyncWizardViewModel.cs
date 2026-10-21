using Basalt.Application.Abstractions;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Sync;
using Basalt.Domain.Accounts;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Sync;

public sealed record ConfigureSyncUseCases(
    IQueryHandler<PreviewSyncLink, SyncLinkPreview> Preview,
    ICommandHandler<ConfigureSync, SyncLinkPreview> Configure);

/// <summary>Preview then confirm linking a vault to a repository (F-015.5).</summary>
public sealed partial class ConfigureSyncWizardViewModel(ViewModelServices services, ConfigureSyncUseCases useCases)
    : ViewModelBase(services)
{
    private VaultId _vault;
    private GitHubAccountId _account;

    [ObservableProperty]
    public partial string Owner { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RepositoryName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Branch { get; set; } = "main";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    public partial SyncLinkPreview? Preview { get; set; }

    [ObservableProperty]
    public partial bool IsMerge { get; private set; }

    [ObservableProperty]
    public partial bool AcceptMerge { get; set; }

    public bool CanConfirm => Preview is { IsArchived: false, CanPush: true };

    public void Attach(VaultId vault, GitHubAccountId account)
    {
        _vault = vault;
        _account = account;
    }

    [RelayCommand]
    public async Task PreviewAsync()
    {
        var result = await RunAsync(ct => useCases.Preview.HandleAsync(
            new PreviewSyncLink(_vault, _account, Reference(), SyncScope.Default, SyncPolicy.Default), ct))
            .ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        Preview = result.Value;
        IsMerge = result.Value.Mode == SyncLinkMode.Merge;
        OnPropertyChanged(nameof(CanConfirm));
    }

    [RelayCommand]
    public async Task ConfigureAsync()
    {
        var result = await RunAsync(ct => useCases.Configure.HandleAsync(
            new ConfigureSync(
                _vault,
                _account,
                Reference(),
                Triggers: new SyncTriggers(),
                AcceptMerge: AcceptMerge), ct)).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        Preview = result.Value;
        Services.Notifications.Show(new Notification(
            NotificationSeverity.Success, Localizer.Get("Sync.Configured")));
    }

    private RemoteRepositoryRef Reference() =>
        new(GitHubHost.GitHubDotCom, Owner.Trim(), RepositoryName.Trim(), Branch.Trim());
}
