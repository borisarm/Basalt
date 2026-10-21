using System.Collections.ObjectModel;
using Basalt.Application.Abstractions;
using Basalt.Application.Accounts;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Presentation.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Sync;

public sealed record AccountsSettingsUseCases(
    IQueryHandler<ListAccounts, IReadOnlyList<GitHubAccount>> List,
    ICommandHandler<RemoveAccount, Unit> Remove);

/// <summary>GitHub accounts on this device: list and sign out (F-015.3).</summary>
public sealed partial class AccountsSettingsViewModel(ViewModelServices services, AccountsSettingsUseCases useCases)
    : ViewModelBase(services)
{
    public ObservableCollection<GitHubAccount> Accounts { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial GitHubAccount? Selected { get; set; }

    public bool IsEmpty => Accounts.Count == 0;

    [RelayCommand]
    public async Task LoadAsync()
    {
        var accounts = await RunAsync(ct => useCases.List.HandleAsync(new ListAccounts(), ct)).ConfigureAwait(true);
        if (accounts.IsFailure)
        {
            return;
        }

        Accounts.Clear();
        foreach (var account in accounts.Value)
        {
            Accounts.Add(account);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync()
    {
        if (Selected is not { } account)
        {
            return;
        }

        var removed = await RunAsync(ct => useCases.Remove.HandleAsync(new RemoveAccount(account.Id), ct))
            .ConfigureAwait(true);
        if (removed.IsFailure)
        {
            return;
        }

        Accounts.Remove(account);
        Selected = null;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool CanRemove() => Selected is not null;
}
