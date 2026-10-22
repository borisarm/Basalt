using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Basalt.Application.Abstractions;
using Basalt.Application.Accounts;
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
    ICommandHandler<ConfigureSync, SyncLinkPreview> Configure,
    IQueryHandler<ListAccounts, IReadOnlyList<GitHubAccount>>? Accounts = null,
    ICommandHandler<CreateRepositoryForVault, RemoteRepositoryInfo>? CreateRepository = null);

/// <summary>Preview then confirm linking a vault to a repository (F-015.5).</summary>
public sealed partial class ConfigureSyncWizardViewModel(ViewModelServices services, ConfigureSyncUseCases useCases)
    : ViewModelBase(services)
{
    private VaultId _vault;
    private GitHubAccountId _account;

    public ObservableCollection<GitHubAccount> Accounts { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateRepositoryCommand))]
    public partial GitHubAccount? SelectedAccount { get; set; }

    [ObservableProperty]
    public partial string Owner { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RepositoryName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Branch { get; set; } = "main";

    [ObservableProperty]
    public partial bool IsPrivate { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm), nameof(PreviewSummary))]
    public partial SyncLinkPreview? Preview { get; set; }

    [ObservableProperty]
    public partial bool IsMerge { get; private set; }

    [ObservableProperty]
    public partial bool AcceptMerge { get; set; }

    [ObservableProperty]
    public partial bool IsConfigured { get; private set; }

    public bool CanConfirm => Preview is { IsArchived: false, CanPush: true };

    public string PreviewSummary => Preview is not { } preview
        ? string.Empty
        : Localizer.Format("Sync.Preview.Summary", preview.Mode, preview.Equal, preview.LocalOnly, preview.RemoteOnly, preview.Conflicts);

    /// <summary>Binds the wizard to the vault and suggests a repository name (spec 07 §8: kebab-case).</summary>
    public void Attach(VaultId vault, string vaultName)
    {
        _vault = vault;
        RepositoryName = Slug(vaultName);
    }

    public void Attach(VaultId vault, GitHubAccountId account)
    {
        _vault = vault;
        _account = account;
    }

    partial void OnSelectedAccountChanged(GitHubAccount? value)
    {
        if (value is not null)
        {
            _account = value.Id;
            Owner = value.Login;
        }
    }

    /// <summary>Lists this device's accounts and preselects the first one (F-015.3).</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (useCases.Accounts is not { } handler)
        {
            return;
        }

        var accounts = await RunAsync(ct => handler.HandleAsync(new ListAccounts(), ct)).ConfigureAwait(true);
        if (accounts.IsFailure)
        {
            return;
        }

        Accounts.Clear();
        foreach (var account in accounts.Value)
        {
            Accounts.Add(account);
        }

        SelectedAccount = Accounts.FirstOrDefault();
    }

    /// <summary>Creates the private repository and prepares the preview (F-015.2).</summary>
    [RelayCommand(CanExecute = nameof(CanCreateRepository))]
    public async Task CreateRepositoryAsync()
    {
        if (useCases.CreateRepository is not { } handler || SelectedAccount is not { } account)
        {
            return;
        }

        var created = await RunAsync(ct => handler.HandleAsync(
            new CreateRepositoryForVault(account.Id, Owner.Trim(), RepositoryName.Trim(), IsPrivate), ct)).ConfigureAwait(true);
        if (created.IsFailure)
        {
            return;
        }

        Owner = created.Value.Owner;
        RepositoryName = created.Value.Name;
        await PreviewAsync().ConfigureAwait(true);
    }

    private bool CanCreateRepository() => useCases.CreateRepository is not null && SelectedAccount is not null;

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
        IsConfigured = true;
        Services.Notifications.Show(new Notification(
            NotificationSeverity.Success, Localizer.Get("Sync.Configured")));
    }

    private RemoteRepositoryRef Reference() =>
        new(GitHubHost.GitHubDotCom, Owner.Trim(), RepositoryName.Trim(), Branch.Trim());

    private static string Slug(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Normalize(NormalizationForm.FormD).ToLower(CultureInfo.InvariantCulture))
        {
            if (char.IsLetterOrDigit(character) && character < 128)
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "vault" : slug;
    }
}
