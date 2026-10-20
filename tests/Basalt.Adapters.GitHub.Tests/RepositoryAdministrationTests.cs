using Basalt.Application.Auth;
using Basalt.Application.Ports.Remote;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Testing.Contracts;
using Basalt.Testing.FakeGitHub;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basalt.Adapters.GitHub.Tests;

public sealed class InMemoryRepositoryAdministrationTests : RepositoryAdministrationContract
{
    protected override Task<IRepositoryAdministration> CreateSutAsync() =>
        Task.FromResult<IRepositoryAdministration>(new InMemoryRepositoryAdministration());
}

public sealed class GitHubRepositoryAdministrationTests : RepositoryAdministrationContract
{
    protected override async Task<IRepositoryAdministration> CreateSutAsync()
    {
        var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        var factory = new GitHubApiClientFactory(
            new StubTokenProvider(server.Options.Token), NullLoggerFactory.Instance);
        var host = GitHubHost.ForTesting(new Uri(server.BaseAddress));
        return new AdministrationSut(new GitHubRepositoryAdministration(factory, host), server, factory);
    }

    [Fact]
    [Trait("Spec", "REPO-ADMIN")]
    public async Task CreateBranch_CreatesItFromTheDefaultHead()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        server.CreateRepo("acme", "vault", new Dictionary<string, string> { ["a.md"] = "a" });
        var factory = new GitHubApiClientFactory(
            new StubTokenProvider(server.Options.Token), NullLoggerFactory.Instance);
        var administration = new GitHubRepositoryAdministration(
            factory, GitHubHost.ForTesting(new Uri(server.BaseAddress)));
        var account = new GitHubAccountId(Guid.NewGuid());

        (await administration.CreateBranchAsync(account, "acme", "vault", "work", Ct)).IsSuccess.Should().BeTrue();

        server.GetRepo("acme", "vault").Head("work").Should().Be(server.GetRepo("acme", "vault").Head());
        (await administration.CreateBranchAsync(account, "acme", "vault", "work", Ct)).Error.Code
            .Should().Be(RemoteErrors.ValidationFailed);
        factory.Dispose();
    }

    private sealed class StubTokenProvider(string token) : IGitHubTokenProvider
    {
        public Task<Result<GitHubToken>> GetTokenAsync(GitHubAccountId account, CancellationToken ct) =>
            Task.FromResult(Result<GitHubToken>.Success(new GitHubToken(token)));
    }

    private sealed class AdministrationSut(
        IRepositoryAdministration inner,
        FakeGitHubServer server,
        GitHubApiClientFactory factory) : IRepositoryAdministration, IAsyncDisposable
    {
        public Task<Result<IReadOnlyList<RemoteRepositoryInfo>>> ListAccessibleRepositoriesAsync(
            GitHubAccountId account,
            CancellationToken ct) => inner.ListAccessibleRepositoriesAsync(account, ct);

        public Task<Result<RemoteRepositoryInfo>> CreateRepositoryAsync(
            GitHubAccountId account,
            string? org,
            string name,
            bool isPrivate,
            CancellationToken ct) => inner.CreateRepositoryAsync(account, org, name, isPrivate, ct);

        public Task<Result<RemoteRepositoryInfo>> GetRepositoryAsync(
            GitHubAccountId account,
            string owner,
            string name,
            CancellationToken ct) => inner.GetRepositoryAsync(account, owner, name, ct);

        public Task<Result<Unit>> CreateBranchAsync(
            GitHubAccountId account,
            string owner,
            string name,
            string branch,
            CancellationToken ct) => inner.CreateBranchAsync(account, owner, name, branch, ct);

        public async ValueTask DisposeAsync()
        {
            factory.Dispose();
            await server.DisposeAsync();
        }
    }
}
