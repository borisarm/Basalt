using System.IO.Compression;
using Basalt.Application.Auth;
using Basalt.Application.Ports.Remote;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Testing.Contracts;
using Basalt.Testing.FakeGitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basalt.Adapters.GitHub.Tests;

public sealed class GitHubRemoteRepositoryTests : RemoteRepositoryContract
{
    protected override async Task<IRemoteRepository> CreateSutAsync()
    {
        var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        server.CreateRepo("acme", "vault");
        var http = new HttpClient(CreateHandler(server)) { BaseAddress = new Uri(server.BaseAddress) };
        var reference = new RemoteRepositoryRef(
            GitHubHost.ForTesting(new Uri(server.BaseAddress)), "acme", "vault", "main");
        var repository = new GitHubRemoteRepository(
            new GitHubApiClient(http, reference, TimeProvider.System), reference);
        return new RepositorySut(repository, server, http);
    }

    [Fact]
    [Trait("Spec", "GITHUB-REMOTE")]
    public async Task TruncatedTree_IsWalkedSubtreeBySubtree()
    {
        await using var server = await FakeGitHubServer.StartAsync(
            new FakeGitHubOptions { TreeTruncationThreshold = 2 }, Ct);
        server.CreateRepo("acme", "vault", new Dictionary<string, string>
        {
            ["a.md"] = "a",
            ["dir/b.md"] = "b",
            ["dir/sub/c.md"] = "c",
        });
        using var http = new HttpClient(CreateHandler(server)) { BaseAddress = new Uri(server.BaseAddress) };
        var reference = new RemoteRepositoryRef(GitHubHost.ForTesting(new Uri(server.BaseAddress)), "acme", "vault", "main");
        var repository = new GitHubRemoteRepository(new GitHubApiClient(http, reference), reference);

        var head = await repository.GetHeadAsync(null, Ct);
        var manifest = await repository.GetTreeAsync(head.Value.Head!.Tree, Ct);

        manifest.IsSuccess.Should().BeTrue();
        manifest.Value.Files.Select(file => file.Path.ToString())
            .Should().BeEquivalentTo("a.md", "dir/b.md", "dir/sub/c.md");
    }

    [Fact]
    [Trait("Spec", "GITHUB-REMOTE")]
    public async Task InvalidRemotePaths_AreQuarantinedInsteadOfFailingTheTree()
    {
        using var http = new HttpClient(new InvalidPathTreeHandler()) { BaseAddress = new Uri("http://localhost") };
        var reference = new RemoteRepositoryRef(GitHubHost.GitHubDotCom, "acme", "vault", "main");
        var repository = new GitHubRemoteRepository(new GitHubApiClient(http, reference), reference);
        var head = await repository.GetHeadAsync(null, Ct);

        var tree = await repository.GetTreeAsync(head.Value.Head!.Tree, Ct);

        tree.Value.Files.Select(file => file.Path.ToString()).Should().Equal("ok.md");
        tree.Value.Quarantined.Should().ContainSingle(issue =>
            issue.Path == "aux.md" && issue.Reason == RemoteQuarantineReasons.InvalidPath);
    }

    [Fact]
    [Trait("Spec", "GITHUB-REMOTE")]
    public async Task Zipball_ExtractsOnlyVaultRelativeFiles()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        server.CreateRepo("acme", "vault", new Dictionary<string, string>
        {
            ["a.md"] = "a",
            ["dir/b.md"] = "b",
        });
        using var http = new HttpClient(CreateHandler(server)) { BaseAddress = new Uri(server.BaseAddress) };
        var reference = new RemoteRepositoryRef(GitHubHost.ForTesting(new Uri(server.BaseAddress)), "acme", "vault", "main");
        var repository = new GitHubRemoteRepository(new GitHubApiClient(http, reference), reference);
        var head = await repository.GetHeadAsync(null, Ct);

        var files = await repository.GetZipballFilesAsync(head.Value.Head!.Commit, Ct);

        files.IsSuccess.Should().BeTrue();
        System.Text.Encoding.UTF8.GetString(files.Value[VaultPath.Create("dir/b.md").Value]).Should().Be("b");
    }

    [Fact]
    [Trait("Spec", "GITHUB-REMOTE")]
    public async Task Zipball_RejectsEntriesThatEscapeTheVault()
    {
        using var http = new HttpClient(new MaliciousZipHandler()) { BaseAddress = new Uri("http://localhost") };
        var reference = new RemoteRepositoryRef(GitHubHost.GitHubDotCom, "acme", "vault", "main");
        var repository = new GitHubRemoteRepository(new GitHubApiClient(http, reference), reference);

        var files = await repository.GetZipballFilesAsync(ParseCommit("0000000000000000000000000000000000000000"), Ct);

        files.Error.Code.Should().Be(RemoteErrors.ValidationFailed);
    }

    [Fact]
    [Trait("Spec", "GITHUB-REMOTE")]
    public async Task CreateTree_IsChunked()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        server.CreateRepo("acme", "vault", new Dictionary<string, string> { ["seed.md"] = "seed" });
        using var http = new HttpClient(CreateHandler(server)) { BaseAddress = new Uri(server.BaseAddress) };
        var reference = new RemoteRepositoryRef(GitHubHost.ForTesting(new Uri(server.BaseAddress)), "acme", "vault", "main");
        var repository = new GitHubRemoteRepository(new GitHubApiClient(http, reference), reference);
        var blob = await repository.CreateBlobAsync(System.Text.Encoding.UTF8.GetBytes("x"), Ct);
        var changes = Enumerable.Range(0, 1_500)
            .Select(i => (TreeChange)new TreeChange.Upsert(VaultPath.Create($"n/{i}.md").Value, blob.Value))
            .ToArray();

        var tree = await repository.CreateTreeAsync(null, changes, Ct);
        var manifest = await repository.GetTreeAsync(tree.Value, Ct);

        tree.IsSuccess.Should().BeTrue();
        manifest.Value.Files.Should().HaveCount(1_500);
        server.Requests.Count(request => request.Method == "POST" && request.Path.EndsWith("/git/trees", StringComparison.Ordinal))
            .Should().Be(2);
    }

    private static CommitSha ParseCommit(string sha) => CommitSha.Parse(sha).Value;

    private sealed class InvalidPathTreeHandler : HttpMessageHandler
    {
        private const string CommitSha = "1111111111111111111111111111111111111111";
        private const string TreeSha = "2222222222222222222222222222222222222222";
        private const string BlobSha = "3333333333333333333333333333333333333333";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = path.EndsWith("/git/ref/heads/main", StringComparison.Ordinal)
                ? "{\"ref\":\"refs/heads/main\",\"object\":{\"sha\":\"" + CommitSha + "\",\"type\":\"commit\"}}"
                : path.Contains("/git/commits/", StringComparison.Ordinal)
                    ? "{\"sha\":\"" + CommitSha + "\",\"message\":\"raw\",\"tree\":{\"sha\":\"" + TreeSha + "\"},\"parents\":[]}"
                    : "{\"sha\":\"" + TreeSha + "\",\"truncated\":false,\"tree\":["
                        + "{\"path\":\"aux.md\",\"mode\":\"100644\",\"type\":\"blob\",\"sha\":\"" + BlobSha + "\",\"size\":1},"
                        + "{\"path\":\"ok.md\",\"mode\":\"100644\",\"type\":\"blob\",\"sha\":\"" + BlobSha + "\",\"size\":2}]}";

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class MaliciousZipHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                Add(zip, "root/../evil.md");
                Add(zip, "root/ok.md");
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(buffer.ToArray()),
            });
        }

        private static void Add(ZipArchive zip, string name)
        {
            var entry = zip.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write("x"u8);
        }
    }
    private static HttpMessageHandler CreateHandler(FakeGitHubServer server)
    {
        var tokens = new StubTokenProvider(Result<GitHubToken>.Success(new GitHubToken(server.Options.Token)));
        var network = new HttpClientHandler();
        var logging = new GitHubLoggingHandler(NullLogger<GitHubLoggingHandler>.Instance) { InnerHandler = network };
        var auth = new GitHubAuthHandler(tokens, new GitHubAccountId(Guid.NewGuid())) { InnerHandler = logging };
        return new GitHubRetryHandler(new GitHubApiOptions { BaseDelay = TimeSpan.Zero }, TimeProvider.System)
        {
            InnerHandler = auth,
        };
    }

    private sealed class StubTokenProvider(Result<GitHubToken> token) : IGitHubTokenProvider
    {
        public Task<Result<GitHubToken>> GetTokenAsync(GitHubAccountId account, CancellationToken ct) =>
            Task.FromResult(token);
    }

    private sealed class RepositorySut(IRemoteRepository inner, FakeGitHubServer server, HttpClient http)
        : IRemoteRepository, IAsyncDisposable
    {
        public Task<Result<RemoteHeadCheck>> GetHeadAsync(string? etag, CancellationToken ct) => inner.GetHeadAsync(etag, ct);

        public Task<Result<RemoteTreeSnapshot>> GetTreeAsync(TreeSha tree, CancellationToken ct) => inner.GetTreeAsync(tree, ct);

        public Task<Result<byte[]>> GetBlobAsync(BlobSha sha, CancellationToken ct) => inner.GetBlobAsync(sha, ct);

        public Task<Result<BlobSha>> CreateBlobAsync(ReadOnlyMemory<byte> content, CancellationToken ct) =>
            inner.CreateBlobAsync(content, ct);

        public Task<Result<TreeSha>> CreateTreeAsync(
            TreeSha? baseTree,
            IReadOnlyList<TreeChange> changes,
            CancellationToken ct) => inner.CreateTreeAsync(baseTree, changes, ct);

        public Task<Result<CommitSha>> CreateCommitAsync(
            string message,
            TreeSha tree,
            IReadOnlyList<CommitSha> parents,
            CommitAuthor author,
            CancellationToken ct) => inner.CreateCommitAsync(message, tree, parents, author, ct);

        public Task<Result<Unit>> UpdateBranchAsync(CommitSha newHead, CommitSha? expectedOld, CancellationToken ct) =>
            inner.UpdateBranchAsync(newHead, expectedOld, ct);

        public Task<Result<IReadOnlyList<RemoteCommitInfo>>> GetFileHistoryAsync(
            VaultPath path,
            int max,
            CancellationToken ct) => inner.GetFileHistoryAsync(path, max, ct);

        public Task<Result<byte[]?>> GetFileAtCommitAsync(VaultPath path, CommitSha commit, CancellationToken ct) =>
            inner.GetFileAtCommitAsync(path, commit, ct);

        public async ValueTask DisposeAsync()
        {
            http.Dispose();
            await server.DisposeAsync();
        }
    }
}
