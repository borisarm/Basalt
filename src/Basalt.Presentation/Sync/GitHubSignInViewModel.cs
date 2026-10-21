using Basalt.Application.Abstractions;
using Basalt.Application.Accounts;
using Basalt.Application.Ports.Auth;
using Basalt.Domain.Accounts;
using Basalt.Presentation.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Sync;

public sealed record GitHubSignInUseCases(
    ICommandHandler<StartGitHubSignIn, DeviceCodeChallenge> Start,
    ICommandHandler<CompleteGitHubSignIn, GitHubAccount> Complete);

/// <summary>OAuth Device Flow: start, show the code, poll until approved (F-015.4).</summary>
public sealed partial class GitHubSignInViewModel(ViewModelServices services, GitHubSignInUseCases useCases)
    : ViewModelBase(services)
{
    [ObservableProperty]
    public partial string? UserCode { get; private set; }

    [ObservableProperty]
    public partial string? VerificationUri { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteCommand))]
    public partial bool IsWaitingForApproval { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    public GitHubAccount? Account { get; private set; }

    public event EventHandler<GitHubAccount>? SignedIn;

    public async Task StartAsync(GitHubHost host)
    {
        var challenge = await RunAsync(ct => useCases.Start.HandleAsync(new StartGitHubSignIn(host), ct))
            .ConfigureAwait(true);
        if (challenge.IsFailure)
        {
            return;
        }

        UserCode = challenge.Value.UserCode;
        VerificationUri = challenge.Value.VerificationUri;
        IsWaitingForApproval = true;
        _ = CompleteAsync(challenge.Value);
    }

    [RelayCommand(CanExecute = nameof(IsWaitingForApproval))]
    private async Task CompleteAsync(DeviceCodeChallenge challenge)
    {
        var account = await RunAsync(ct => useCases.Complete.HandleAsync(new CompleteGitHubSignIn(challenge), ct))
            .ConfigureAwait(true);
        IsWaitingForApproval = false;
        if (account.IsFailure)
        {
            ErrorMessage = Localizer.Error(account.Error);
            return;
        }

        Account = account.Value;
        UserCode = null;
        SignedIn?.Invoke(this, account.Value);
    }
}
