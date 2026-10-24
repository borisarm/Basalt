using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Basalt.Domain.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Basalt.Testing.FakeGitHub;

/// <summary>
/// In-memory GitHub API used by integration tests (spec 06 Â§14). Runs on Kestrel with a real loopback port or on
/// <see cref="TestServer"/>.
/// </summary>
public sealed class FakeGitHubServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, FakeRepository> _repos = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _redirects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _issuedTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RequestRecord> _requests = new();
    private readonly List<Func<FakeRepository, Task>> _beforeUpdateRef = [];
    private readonly object _hookGate = new();
    private HttpClient? _testClient;
    private int _nextRepoId;
    internal int RateRemaining;
    private bool _disposed;

    private FakeGitHubServer(WebApplication app, FakeGitHubOptions options, HttpClient? testClient)
    {
        _app = app;
        Options = options;
        RateRemaining = options.RateLimit;
        _testClient = testClient;
    }

    public FakeGitHubOptions Options { get; }

    public FaultPlan Faults { get; } = new();

    public FakeDeviceFlow DeviceFlow { get; } = new();

    public string BaseAddress { get; private set; } = "http://localhost";

    public IReadOnlyList<RequestRecord> Requests => _requests.ToArray();

    public IReadOnlyCollection<FakeRepository> Repositories => _repos.Values.ToArray();

    public static async Task<FakeGitHubServer> StartAsync(
        FakeGitHubOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new FakeGitHubOptions();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var server = new FakeGitHubServer(app, options, null);
        FakeGitHubEndpoints.Map(app, server);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        server.BaseAddress = address ?? "http://127.0.0.1";
        return server;
    }

    public static async Task<FakeGitHubServer> StartTestServerAsync(
        FakeGitHubOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new FakeGitHubOptions();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        var server = new FakeGitHubServer(app, options, null);
        FakeGitHubEndpoints.Map(app, server);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        var client = app.GetTestClient();
        server.BaseAddress = client.BaseAddress!.ToString().TrimEnd('/');
        server._testClient = client;
        return server;
    }

    public HttpClient CreateClient(string? token = null)
    {
        if (_testClient is not null)
        {
            if (token is not null)
            {
                _testClient.DefaultRequestHeaders.Authorization = new("Bearer", token);
            }

            return _testClient;
        }

        var client = new HttpClient { BaseAddress = new Uri(BaseAddress) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Basalt.Tests/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }

        return client;
    }

    public FakeRepository CreateRepo(
        string owner,
        string name,
        bool isPrivate = true,
        bool allowPush = true)
    {
        var repository = new FakeRepository(Interlocked.Increment(ref _nextRepoId), owner, name, isPrivate, allowPush);
        if (!_repos.TryAdd(Key(owner, name), repository))
        {
            throw new InvalidOperationException($"Repository {owner}/{name} already exists.");
        }

        return repository;
    }

    public FakeRepository CreateRepo(
        string owner,
        string name,
        IReadOnlyDictionary<string, string> files,
        string message = "Initial commit",
        bool isPrivate = true,
        bool allowPush = true)
    {
        var repository = CreateRepo(owner, name, isPrivate, allowPush);
        CommitFiles(owner, name, repository.DefaultBranch, files, message);
        return repository;
    }

    public FakeRepository GetRepo(string owner, string name) =>
        _repos.TryGetValue(Key(owner, name), out var repository)
            ? repository
            : throw new KeyNotFoundException($"Repository {owner}/{name} does not exist.");

    public bool TryGetRepo(string owner, string name, out FakeRepository repository) =>
        _repos.TryGetValue(Key(owner, name), out repository!);

    public void RenameRepo(string owner, string name, string newName)
    {
        var key = Key(owner, name);
        if (!_repos.TryRemove(key, out var repository))
        {
            throw new KeyNotFoundException($"Repository {owner}/{name} does not exist.");
        }

        _redirects[Key(owner, name)] = Key(owner, newName);
        repository.Name = newName;
        _repos[Key(owner, newName)] = repository;
    }

    public void DeleteRepo(string owner, string name)
    {
        _repos.TryRemove(Key(owner, name), out _);
    }

    public string CommitFiles(
        string owner,
        string name,
        string branch,
        IReadOnlyDictionary<string, string> files,
        string message = "Commit from another device",
        GitSignature? author = null,
        GitSignature? committer = null) =>
        CommitFiles(
            owner,
            name,
            branch,
            files.ToDictionary(pair => pair.Key, pair => Encoding.UTF8.GetBytes(pair.Value), StringComparer.Ordinal),
            message,
            author,
            committer);

    public string CommitFiles(
        string owner,
        string name,
        string branch,
        IReadOnlyDictionary<string, byte[]> files,
        string message = "Commit from another device",
        GitSignature? author = null,
        GitSignature? committer = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var repository = GetRepo(owner, name);
        var bytes = files;
        var head = repository.Head(branch);
        var parents = head is null ? new List<CommitSha>() : [CommitSha.Parse(head).Value];
        var merged = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (head is not null)
        {
            foreach (var (path, content) in FakeGitStore.Flatten(repository, FakeGitStore.FindCommit(repository, head)!.Tree))
            {
                merged[path] = content;
            }
        }

        foreach (var (path, content) in bytes)
        {
            merged[FakeGitStore.NormalizePath(path)] = content;
        }

        var commit = FakeGitStore.CreateCommit(
            repository, merged, parents, message, author ?? Options.DefaultSignature, committer ?? author ?? Options.DefaultSignature);
        FakeGitStore.SetRef(repository, branch, commit.ToString());
        return commit.ToString();
    }

    public string ForcePush(
        string owner,
        string name,
        string branch,
        IReadOnlyDictionary<string, string> files,
        string message = "Forced update")
    {
        var repository = GetRepo(owner, name);
        var bytes = files.ToDictionary(pair => pair.Key, pair => Encoding.UTF8.GetBytes(pair.Value), StringComparer.Ordinal);
        var commit = FakeGitStore.CreateCommit(
            repository, bytes, [], message, Options.DefaultSignature, Options.DefaultSignature);
        FakeGitStore.SetRef(repository, branch, commit.ToString());
        return commit.ToString();
    }

    public IReadOnlyDictionary<string, string> GetFiles(string owner, string name, string? branch = null)
    {
        var repository = GetRepo(owner, name);
        var head = repository.Head(branch);
        if (head is null)
        {
            return new Dictionary<string, string>();
        }

        var commit = FakeGitStore.FindCommit(repository, head)!;
        return FakeGitStore.Flatten(repository, commit.Tree)
            .ToDictionary(pair => pair.Key, pair => Encoding.UTF8.GetString(pair.Value), StringComparer.Ordinal);
    }

    public void BeforeUpdateRef(Action<FakeRepository> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_hookGate)
        {
            _beforeUpdateRef.Add(repository =>
            {
                callback(repository);
                return Task.CompletedTask;
            });
        }
    }

    public void BeforeUpdateRef(Func<FakeRepository, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_hookGate)
        {
            _beforeUpdateRef.Add(callback);
        }
    }

    public void ClearBeforeUpdateRef()
    {
        lock (_hookGate)
        {
            _beforeUpdateRef.Clear();
        }
    }

    internal async Task InvokeBeforeUpdateRefAsync(FakeRepository repository)
    {
        Func<FakeRepository, Task>[] hooks;
        lock (_hookGate)
        {
            hooks = [.. _beforeUpdateRef];
        }

        foreach (var hook in hooks)
        {
            await hook(repository).ConfigureAwait(false);
        }
    }

    internal void IssueToken(string token) => _issuedTokens.TryAdd(token, 0);

    internal bool IsAuthorized(HttpRequest request)
    {
        if (!Options.RequireAuthentication)
        {
            return true;
        }

        var header = request.Headers["Authorization"].ToString();
        if (header.Length == 0)
        {
            return false;
        }

        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..]
            : header.StartsWith("token ", StringComparison.OrdinalIgnoreCase)
                ? header["token ".Length..]
                : header;
        return token == Options.Token || _issuedTokens.ContainsKey(token);
    }

    internal FakeRepository? FindRepo(string owner, string name) =>
        _repos.TryGetValue(Key(owner, name), out var repository) ? repository : null;

    internal string? RedirectFor(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || !segments[0].Equals("repos", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return _redirects.TryGetValue(Key(segments[1], segments[2]), out var target) ? target : null;
    }

    internal void Record(RequestRecord record) => _requests.Enqueue(record);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _testClient?.Dispose();
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private static string Key(string owner, string name) => owner + "/" + name;
}
