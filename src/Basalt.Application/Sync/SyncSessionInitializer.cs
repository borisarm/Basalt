using Basalt.Application.Accounts;
using Basalt.Application.Auth;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;

namespace Basalt.Application.Sync;

/// <summary>Attaches the configured repository and resumes automatic sync when a vault opens (spec 06 §11).</summary>
public sealed class SyncSessionInitializer(
    ISyncProfileStore profiles,
    IRemoteRepositoryFactory repositories,
    IGitHubTokenProvider tokens,
    IAccountStore accounts,
    SyncCoordinator coordinator) : IVaultSessionInitializer
{
    public async Task<Result<Unit>> InitializeAsync(VaultSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var profile = await profiles.GetAsync(session.Id, ct).ConfigureAwait(false);
        if (profile is null || !profile.Enabled || profile.Paused)
        {
            return Result.Success();
        }

        session.Set(repositories.Create(profile.AccountId, profile.Repository));
        var token = await tokens.GetTokenAsync(profile.AccountId, ct).ConfigureAwait(false);
        var account = await accounts.GetAsync(profile.AccountId, ct).ConfigureAwait(false);
        var author = new CommitAuthor(
            profile.Commit.AuthorNameOverride ?? account.ValueOrDefault()?.Login ?? "Basalt",
            profile.Commit.AuthorEmailOverride
                ?? (account.ValueOrDefault() is { } saved
                    ? $"{saved.Login}@users.noreply.github.com"
                    : "basalt@users.noreply.github.com"));
        await coordinator.StartAsync(
            profile,
            new SyncCycleContext(true, token.IsSuccess, author, session.Name)).ConfigureAwait(false);
        return Result.Success();
    }
}

file static class SyncSessionAccountExtensions
{
    public static Domain.Accounts.GitHubAccount? ValueOrDefault(this Result<Domain.Accounts.GitHubAccount> result) =>
        result.IsSuccess ? result.Value : null;
}
