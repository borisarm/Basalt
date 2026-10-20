using System.Collections.Concurrent;
using System.Net;
using Basalt.Application.Auth;
using Basalt.Application.Ports.Remote;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Basalt.Adapters.GitHub;

/// <summary>Builds (and caches) one authenticated HTTP pipeline per account and API host (spec 06 §9.2).</summary>
internal sealed class GitHubApiClientFactory : IDisposable
{
    private readonly IGitHubTokenProvider _tokens;
    private readonly ILoggerFactory _loggers;
    private readonly TimeProvider _clock;
    private readonly GitHubApiOptions _options;
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private bool _disposed;

    public GitHubApiClientFactory(IGitHubTokenProvider tokens, ILoggerFactory loggers)
    {
        _tokens = tokens;
        _loggers = loggers;
        _clock = TimeProvider.System;
        _options = new GitHubApiOptions();
    }

    public GitHubApiClient Create(GitHubAccountId account, RemoteRepositoryRef repository) =>
        new(HttpClient(account, repository.Host), repository, _clock);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }
    }

    private HttpClient HttpClient(GitHubAccountId account, GitHubHost host)
    {
        var key = account + "|" + host.ApiUrl;
        return _clients.GetOrAdd(key, _ =>
        {
            var network = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.All,
            };
            var logging = new GitHubLoggingHandler(_loggers.CreateLogger<GitHubLoggingHandler>()) { InnerHandler = network };
            var auth = new GitHubAuthHandler(_tokens, account) { InnerHandler = logging };
            var retry = new GitHubRetryHandler(_options, _clock) { InnerHandler = auth };
            return new HttpClient(retry) { BaseAddress = host.ApiUrl };
        });
    }
}

/// <summary>Until the account store/token provider is wired (T8.6), remote calls fail with <c>remote.unauthorized</c>.</summary>
internal sealed class UnavailableGitHubTokenProvider : IGitHubTokenProvider
{
    public Task<Result<GitHubToken>> GetTokenAsync(GitHubAccountId account, CancellationToken ct) =>
        Task.FromResult(Result<GitHubToken>.Failure(
            new Error(RemoteErrors.Unauthorized, "No GitHub account is signed in.")));
}
