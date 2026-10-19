using Basalt.Application.Errors;
using Basalt.Application.Ports.Storage;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;

namespace Basalt.Application.Accounts;

/// <summary>Persisted accounts (<c>{AppData}/accounts.json</c>, never tokens).</summary>
public sealed record AccountsDocument(IReadOnlyList<GitHubAccount> Accounts)
{
    public static AccountsDocument Empty { get; } = new([]);
}

/// <summary>CRUD of GitHub accounts registered on this device (spec 03 §1.6).</summary>
public interface IAccountStore
{
    Task<IReadOnlyList<GitHubAccount>> GetAllAsync(CancellationToken ct);

    /// <returns><c>account.not_found</c> when unknown.</returns>
    Task<Result<GitHubAccount>> GetAsync(GitHubAccountId id, CancellationToken ct);

    /// <summary>Adds the account or replaces the one with the same id.</summary>
    Task<Result<Unit>> SaveAsync(GitHubAccount account, CancellationToken ct);

    /// <returns><c>account.not_found</c> when unknown.</returns>
    Task<Result<Unit>> RemoveAsync(GitHubAccountId id, CancellationToken ct);
}

/// <summary><see cref="IAccountStore"/> over a document store; operations are serialised.</summary>
public sealed class AccountStore(IDocumentStore<AccountsDocument> store) : IAccountStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<GitHubAccount>> GetAllAsync(CancellationToken ct) =>
        (await store.LoadAsync(ct).ConfigureAwait(false)).Value.Accounts;

    public async Task<Result<GitHubAccount>> GetAsync(GitHubAccountId id, CancellationToken ct) =>
        (await GetAllAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == id) is { } account
            ? account
            : NotFound();

    public async Task<Result<Unit>> SaveAsync(GitHubAccount account, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var accounts = (await store.LoadAsync(ct).ConfigureAwait(false)).Value.Accounts.ToList();
            var index = accounts.FindIndex(a => a.Id == account.Id);
            if (index >= 0)
            {
                accounts[index] = account;
            }
            else
            {
                accounts.Add(account);
            }

            return await store.SaveAsync(new AccountsDocument(accounts), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<Unit>> RemoveAsync(GitHubAccountId id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var accounts = (await store.LoadAsync(ct).ConfigureAwait(false)).Value.Accounts.ToList();
            return accounts.RemoveAll(a => a.Id == id) == 0
                ? NotFound()
                : await store.SaveAsync(new AccountsDocument(accounts), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static Error NotFound() => new(ErrorCodes.AccountNotFound, "The account does not exist.");
}
