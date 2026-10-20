using System.Text;
using Basalt.Application.Ports.Auth;
using Basalt.Application.Ports.Remote;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Testing.FakeGitHub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basalt.Adapters.GitHub.Tests;

public sealed class GitHubApiClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Spec", "GITHUB-CLIENT")]
    public async Task GetUser_SendsBearerTokenAndReadsRateLimitHeaders()
    {
        await using var server = await FakeGitHubServer.StartAsync(new FakeGitHubOptions { RateLimit = 10 }, Ct);
        var (api, http, _) = Create(server);
        using var _http = http;

        var user = await api.GetUserAsync(Ct);

        user.IsSuccess.Should().BeTrue();
        user.Value.Login.Should().Be(server.Options.Login);
        api.LastRateLimit!.Remaining.Should().Be(9);
        server.Requests[^1].Authorization.Should().Be("Bearer " + server.Options.Token);
        server.Requests.Should().OnlyContain(request => !request.Query.Contains(server.Options.Token, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Spec", "GITHUB-CLIENT")]
    public async Task Statuses_MapToTheErrorCatalog()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        var unauthorized = Create(server, tokens: new StubTokenProvider(
            Result<GitHubToken>.Failure(new Error(RemoteErrors.Unauthorized, "no token"))));
        using var http401 = unauthorized.Http;
        (await unauthorized.Api.GetUserAsync(Ct)).Error.Code.Should().Be(RemoteErrors.Unauthorized);

        var (api, http, _) = Create(server);
        using var _http = http;
        server.CreateRepo("acme", "vault", new Dictionary<string, string> { ["a.md"] = "a" });

        server.Faults.FailNext("GET", "/repos/acme/vault", status: 403);
        (await api.GetRepositoryAsync("acme", "vault", Ct)).Error.Code.Should().Be(RemoteErrors.Forbidden);

        (await api.GetRepositoryAsync("acme", "missing", Ct)).Error.Code.Should().Be(RemoteErrors.NotFound);

        server.CreateRepo("acme", "empty");
        var (emptyApi, emptyHttp, _) = Create(server, repositoryName: "empty");
        using var _emptyHttp = emptyHttp;
        (await emptyApi.GetCommitAsync(ParseCommit("0000000000000000000000000000000000000000"), Ct)).Error.Code
            .Should().Be(RemoteErrors.RepositoryEmpty);

        (await api.UpdateRefAsync("main", ParseCommit("0000000000000000000000000000000000000000"), false, Ct)).Error.Code
            .Should().Be(RemoteErrors.ValidationFailed);

        var first = server.GetRepo("acme", "vault").Head()!;
        var second = server.CommitFiles("acme", "vault", "main", new Dictionary<string, string> { ["a.md"] = "b" });
        (await api.UpdateRefAsync("main", ParseCommit(first), false, Ct)).Error.Code
            .Should().Be(RemoteErrors.NonFastForward);
        server.GetRepo("acme", "vault").Head().Should().Be(second);
    }

    [Theory]
    [InlineData(429, RemoteErrors.RateLimited)]
    [InlineData(500, RemoteErrors.ServerError)]
    [InlineData(503, RemoteErrors.ServerError)]
    [Trait("Spec", "GITHUB-CLIENT")]
    public async Task TransientFailures_MapWhenRetriesAreExhausted(int status, string expected)
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        var options = new GitHubApiOptions { MaxRetries = 0, BaseDelay = TimeSpan.Zero };
        var (api, http, _) = Create(server, options: options);
        using var _http = http;
        server.Faults.FailNext("GET", "/user", status: status, retryAfter: status == 429 ? TimeSpan.Zero : null);

        var result = await api.GetUserAsync(Ct);

        result.Error.Code.Should().Be(expected);
        if (status == 429)
        {
            result.Error.Metadata!["retry_at"].Should().NotBeNull();
        }
    }

    [Fact]
    [Trait("Spec", "GITHUB-CLIENT")]
    public async Task NetworkAndTimeout_MapToTheirOwnCodes()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        var options = new GitHubApiOptions { MaxRetries = 0, BaseDelay = TimeSpan.Zero };
        var (api, http, _) = Create(server, options: options);
        using var _http = http;

        server.Faults.DropConnectionNext("GET", "/user");
        (await api.GetUserAsync(Ct)).Error.Code.Should().Be(RemoteErrors.Network);

        var (slowApi, slowHttp, _) = Create(server, options: options);
        using var _slowHttp = slowHttp;
        slowHttp.Timeout = TimeSpan.FromMilliseconds(50);
        server.Faults.FailNext("GET", "/user", delay: TimeSpan.FromMilliseconds(500));
        (await slowApi.GetUserAsync(Ct)).Error.Code.Should().Be(RemoteErrors.Timeout);
    }

    [Fact]
    [Trait("Spec", "GITHUB-CLIENT")]
    public async Task GetRequests_Retry_ButPatchRefDoesNot()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        var options = new GitHubApiOptions { MaxRetries = 3, BaseDelay = TimeSpan.Zero };
        var (api, http, _) = Create(server, options: options);
        using var _http = http;
        server.CreateRepo("acme", "vault", new Dictionary<string, string> { ["a.md"] = "a" });

        server.Faults.FailNth("GET", "/user", 1, status: 500, times: 2);
        (await api.GetUserAsync(Ct)).IsSuccess.Should().BeTrue();
        server.Requests.Count(request => request.Method == "GET" && request.Path == "/user").Should().Be(3);

        var head = ParseCommit(server.GetRepo("acme", "vault").Head()!);
        server.Faults.FailNext("PATCH", "/git/refs/heads/main", status: 500);
        (await api.UpdateRefAsync("main", head, force: false, Ct)).Error.Code.Should().Be(RemoteErrors.ServerError);
        server.Requests.Count(request => request.Method == "PATCH").Should().Be(1);
    }

    [Fact]
    [Trait("Spec", "GITHUB-CLIENT")]
    public async Task RefETag_ReturnsNotModified()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        var (api, http, _) = Create(server);
        using var _http = http;
        server.CreateRepo("acme", "vault", new Dictionary<string, string> { ["a.md"] = "a" });

        var first = await api.GetRefAsync(null, Ct);

        first.IsSuccess.Should().BeTrue();
        first.Value.NotModified.Should().BeFalse();
        first.Value.ETag.Should().NotBeNull();

        var second = await api.GetRefAsync(first.Value.ETag, Ct);

        second.IsSuccess.Should().BeTrue();
        second.Value.NotModified.Should().BeTrue();
        second.Value.Value.Should().BeNull();
    }

    [Fact]
    [Trait("Spec", "GITHUB-CLIENT")]
    public async Task Repositories_ArePaginated()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        var (api, http, _) = Create(server);
        using var _http = http;
        server.CreateRepo("acme", "one");
        server.CreateRepo("acme", "two");
        server.CreateRepo("acme", "three");

        var first = await api.ListRepositoriesAsync(1, 2, Ct);
        var second = await api.ListRepositoriesAsync(2, 2, Ct);

        first.Value.Items.Should().HaveCount(2);
        first.Value.Next.Should().Contain("page=2");
        second.Value.Items.Should().ContainSingle();
    }

    [Fact]
    [Trait("Spec", "GITHUB-CLIENT")]
    public async Task Logs_RedactTheAuthorizationHeaderAndSecretQueryParameters()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        var logger = new CapturingLogger<GitHubLoggingHandler>();
        var (api, http, _) = Create(server, logger: logger);
        using var _http = http;

        await api.GetUserAsync(Ct);
        server.Faults.FailNext("GET", "/user", status: 401);
        await api.GetUserAsync(Ct);

        logger.Messages.Should().Contain(message => message.Contains("Authorization: ***", StringComparison.Ordinal));
        logger.Messages.Should().OnlyContain(message => !message.Contains(server.Options.Token, StringComparison.Ordinal));
        GitHubLoggingHandler.Redact(new Uri("https://api.github.com/user?access_token=secret&x=1"))
            .Should().Be("https://api.github.com/user?access_token=***&x=1");
    }

    [Fact]
    [Trait("Spec", "GITHUB-CLIENT")]
    public async Task TreeCreation_CommitAndRefUpdate_WorkThroughTheClient()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        var (api, http, _) = Create(server);
        using var _http = http;
        server.CreateRepo("acme", "vault", new Dictionary<string, string> { ["a.md"] = "one" });
        var first = server.GetRepo("acme", "vault").Head()!;
        var commit = await api.GetCommitAsync(ParseCommit(first), Ct);
        var baseTree = ParseTree(commit.Value.Tree.Sha);

        var blob = await api.CreateBlobAsync(Encoding.UTF8.GetBytes("two"), Ct);
        var tree = await api.CreateTreeAsync(
            baseTree,
            [new TreeChange.Upsert(VaultPath.Create("dir/b.md").Value, BlobSha.Parse(blob.Value.Sha).Value), new TreeChange.Delete(VaultPath.Create("a.md").Value)],
            Ct);
        var next = await api.CreateCommitAsync(
            "Second", ParseTree(tree.Value.Sha), [ParseCommit(first)], new CommitAuthor("Basalt", "basalt@example.com"), Ct);
        (await api.UpdateRefAsync("main", ParseCommit(next.Value.Sha), false, Ct)).IsSuccess.Should().BeTrue();

        server.GetFiles("acme", "vault").Should().ContainKey("dir/b.md").WhoseValue.Should().Be("two");
        server.GetFiles("acme", "vault").Should().NotContainKey("a.md");
    }

    [Fact]
    [Trait("Spec", "GITHUB-CLIENT")]
    public async Task DeviceFlow_And_TruncatedTree_AreExposed()
    {
        await using var server = await FakeGitHubServer.StartAsync(new FakeGitHubOptions { TreeTruncationThreshold = 1 }, Ct);
        var (api, http, _) = Create(server);
        using var _http = http;
        server.DeviceFlow.ApproveAfterPolls = 2;

        var code = await api.StartDeviceFlowAsync("client", "repo", Ct);
        code.Value.DeviceCode.Should().Be(server.DeviceFlow.DeviceCode);
        (await api.PollDeviceTokenAsync("client", code.Value.DeviceCode, Ct)).Value.Error.Should().Be("authorization_pending");
        (await api.PollDeviceTokenAsync("client", code.Value.DeviceCode, Ct)).Value.AccessToken.Should()
            .Be(server.DeviceFlow.AccessToken);

        var repository = server.CreateRepo("acme", "vault", new Dictionary<string, string>
        {
            ["a.md"] = "a",
            ["b.md"] = "b",
        });
        var commit = await api.GetCommitAsync(ParseCommit(repository.Head()!), Ct);
        var tree = await api.GetTreeAsync(ParseTree(commit.Value.Tree.Sha), recursive: true, Ct);

        tree.Value.Truncated.Should().BeTrue();
        tree.Value.Tree.Should().ContainSingle();
    }

    private static (GitHubApiClient Api, HttpClient Http, StubTokenProvider Tokens) Create(
        FakeGitHubServer server,
        ILogger<GitHubLoggingHandler>? logger = null,
        IGitHubTokenProvider? tokens = null,
        GitHubApiOptions? options = null,
        string repositoryName = "vault")
    {
        var tokenProvider = tokens as StubTokenProvider
            ?? new StubTokenProvider(Result<GitHubToken>.Success(new GitHubToken(server.Options.Token)));
        var network = new HttpClientHandler();
        var logging = new GitHubLoggingHandler(logger ?? NullLogger<GitHubLoggingHandler>.Instance) { InnerHandler = network };
        var auth = new GitHubAuthHandler(tokens ?? tokenProvider, new GitHubAccountId(Guid.NewGuid())) { InnerHandler = logging };
        var retry = new GitHubRetryHandler(options ?? new GitHubApiOptions { BaseDelay = TimeSpan.Zero }, TimeProvider.System)
        {
            InnerHandler = auth,
        };
        var http = new HttpClient(retry) { BaseAddress = new Uri(server.BaseAddress) };
        var host = GitHubHost.ForTesting(new Uri(server.BaseAddress));
        var reference = new RemoteRepositoryRef(host, "acme", repositoryName, "main");
        return (new GitHubApiClient(http, reference, TimeProvider.System), http, tokenProvider);
    }

    private static CommitSha ParseCommit(string sha) => CommitSha.Parse(sha).Value;

    private static TreeSha ParseTree(string sha) => TreeSha.Parse(sha).Value;

    private sealed class StubTokenProvider(Result<GitHubToken> token) : IGitHubTokenProvider
    {
        public Task<Result<GitHubToken>> GetTokenAsync(GitHubAccountId account, CancellationToken ct) =>
            Task.FromResult(token);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
