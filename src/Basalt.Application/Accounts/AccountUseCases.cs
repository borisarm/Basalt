using Basalt.Application.Abstractions;
using Basalt.Application.Auth;
using Basalt.Application.Ports.Auth;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Remote;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;

namespace Basalt.Application.Accounts;

/// <summary>Starts the OAuth Device Flow and returns the code the user must enter (spec 06 §9.1).</summary>
public sealed record StartGitHubSignIn(GitHubHost Host) : ICommand<DeviceCodeChallenge>;

/// <summary>Polls until the user approves, then stores the account and the token (spec 06 §9.1).</summary>
public sealed record CompleteGitHubSignIn(DeviceCodeChallenge Challenge) : ICommand<GitHubAccount>;

public sealed record ListAccounts : IQuery<IReadOnlyList<GitHubAccount>>;

/// <summary>Removes the account and its token; vault profiles are left for the user to reconfigure (spec 03 §2).</summary>
public sealed record RemoveAccount(GitHubAccountId AccountId) : ICommand<Unit>;

public sealed record ListRepositories(GitHubAccountId AccountId) : IQuery<IReadOnlyList<RemoteRepositoryInfo>>;

public sealed record CreateRepositoryForVault(
    GitHubAccountId AccountId,
    string? Owner,
    string Name,
    bool Private = true) : ICommand<RemoteRepositoryInfo>;

public sealed class StartGitHubSignInHandler(IGitHubAuthenticator authenticator)
    : ICommandHandler<StartGitHubSignIn, DeviceCodeChallenge>
{
    public Task<Result<DeviceCodeChallenge>> HandleAsync(StartGitHubSignIn command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return authenticator.StartDeviceFlowAsync(command.Host, ct);
    }
}

public sealed class CompleteGitHubSignInHandler(
    IGitHubAuthenticator authenticator,
    IGitHubTokenProvider tokens,
    IAccountStore accounts,
    IIdGenerator ids,
    IClock clock,
    TimeProvider time) : ICommandHandler<CompleteGitHubSignIn, GitHubAccount>
{
    private const int MaxPolls = 120;

    public async Task<Result<GitHubAccount>> HandleAsync(CompleteGitHubSignIn command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var challenge = command.Challenge;
        var interval = challenge.Interval;
        for (var attempt = 0; attempt < MaxPolls; attempt++)
        {
            var polled = await authenticator.PollForTokenAsync(challenge, ct).ConfigureAwait(false);
            if (polled.IsFailure)
            {
                if (polled.Error.Code == AuthErrors.AuthorizationPending)
                {
                    await Task.Delay(interval, time, ct).ConfigureAwait(false);
                    continue;
                }

                if (polled.Error.Code == AuthErrors.SlowDown)
                {
                    interval += TimeSpan.FromSeconds(5);
                    await Task.Delay(interval, time, ct).ConfigureAwait(false);
                    continue;
                }

                return polled.Error;
            }

            var user = await authenticator.GetUserAsync(challenge.Host, polled.Value, ct).ConfigureAwait(false);
            if (user.IsFailure)
            {
                return user.Error;
            }

            var account = new GitHubAccount(
                new GitHubAccountId(ids.NewId()),
                user.Value.Login,
                challenge.Host,
                user.Value.AvatarUrl,
                user.Value.Scopes,
                clock.UtcNow);
            var saved = await accounts.SaveAsync(account, ct).ConfigureAwait(false);
            if (saved.IsFailure)
            {
                return saved.Error;
            }

            var stored = await tokens.StoreAsync(account.Id, challenge.Host, polled.Value, ct).ConfigureAwait(false);
            return stored.IsFailure ? stored.Error : account;
        }

        return new Error(AuthErrors.ExpiredToken, "The device code expired before approval.");
    }
}

public sealed class ListAccountsHandler(IAccountStore accounts) : IQueryHandler<ListAccounts, IReadOnlyList<GitHubAccount>>
{
    public async Task<Result<IReadOnlyList<GitHubAccount>>> HandleAsync(ListAccounts query, CancellationToken ct)
    {
        var all = await accounts.GetAllAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<GitHubAccount>>.Success(all);
    }
}

public sealed class RemoveAccountHandler(IAccountStore accounts, IGitHubTokenProvider tokens)
    : ICommandHandler<RemoveAccount, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RemoveAccount command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var removed = await accounts.RemoveAsync(command.AccountId, ct).ConfigureAwait(false);
        if (removed.IsFailure)
        {
            return removed;
        }

        var token = await tokens.RemoveAsync(command.AccountId, ct).ConfigureAwait(false);
        return token.IsFailure ? token : Result.Success();
    }
}

public sealed class ListRepositoriesHandler(IRepositoryAdministration repositories)
    : IQueryHandler<ListRepositories, IReadOnlyList<RemoteRepositoryInfo>>
{
    public Task<Result<IReadOnlyList<RemoteRepositoryInfo>>> HandleAsync(ListRepositories query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return repositories.ListAccessibleRepositoriesAsync(query.AccountId, ct);
    }
}

public sealed class CreateRepositoryForVaultHandler(IRepositoryAdministration repositories)
    : ICommandHandler<CreateRepositoryForVault, RemoteRepositoryInfo>
{
    public Task<Result<RemoteRepositoryInfo>> HandleAsync(CreateRepositoryForVault command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return repositories.CreateRepositoryAsync(command.AccountId, command.Owner, command.Name, command.Private, ct);
    }
}
