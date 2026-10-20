using System.Net;
using System.Net.Http.Headers;
using Basalt.Application.Ports.Auth;
using Basalt.Domain.Accounts;
using Microsoft.Extensions.Logging;

namespace Basalt.Adapters.GitHub;

/// <summary>Retry, auth and logging configuration of the GitHub HTTP client (spec 06 §9.2).</summary>
internal sealed class GitHubApiOptions
{
    public int MaxRetries { get; set; } = 3;

    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    public bool RespectRetryAfter { get; set; } = true;
}

/// <summary>Last <c>x-ratelimit-*</c> values seen by the client (spec 06 §9.2).</summary>
internal sealed record GitHubRateLimit(int Limit, int Remaining, int Used, DateTimeOffset Reset);

/// <summary>Adds <c>Authorization: Bearer</c>; the token never goes in the URL (spec 06 §9.1).</summary>
internal sealed class GitHubAuthHandler : DelegatingHandler
{
    private readonly IGitHubTokenProvider _tokens;
    private readonly GitHubAccountId _account;

    public GitHubAuthHandler(IGitHubTokenProvider tokens, GitHubAccountId account)
    {
        _tokens = tokens;
        _account = account;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await _tokens.GetTokenAsync(_account, ct).ConfigureAwait(false);
        if (token.IsSuccess)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value.Value);
        }

        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}

/// <summary>Logs method, URL and headers, redacting credentials (spec 06 §9.1 and §15).</summary>
internal sealed class GitHubLoggingHandler : DelegatingHandler
{
    private static readonly HashSet<string> SecretQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "access_token", "token", "client_secret", "refresh_token",
    };

    private readonly ILogger<GitHubLoggingHandler> _logger;

    public GitHubLoggingHandler(ILogger<GitHubLoggingHandler> logger) => _logger = logger;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var headers = string.Join(
                ", ",
                request.Headers.Select(header => header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    ? $"{header.Key}: ***"
                    : $"{header.Key}: {string.Join(' ', header.Value)}"));
            _logger.LogDebug(
                "GitHub {Method} {Uri} ({Headers})",
                request.Method.Method,
                Redact(request.RequestUri),
                headers);
        }

        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }

    internal static string Redact(Uri? uri)
    {
        if (uri is null || string.IsNullOrEmpty(uri.Query))
        {
            return uri?.ToString() ?? string.Empty;
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var redacted = query.Select(part =>
        {
            var equals = part.IndexOf('=');
            var key = equals < 0 ? part : part[..equals];
            return SecretQueryKeys.Contains(Uri.UnescapeDataString(key)) ? key + "=***" : part;
        });
        var builder = new UriBuilder(uri) { Query = string.Join('&', redacted) };
        return builder.Uri.ToString();
    }
}

/// <summary>
/// Retries only idempotent requests and the content-addressed POSTs (blobs, trees, commits); PATCH ref is never retried
/// (spec 06 §9.2).
/// </summary>
internal sealed class GitHubRetryHandler : DelegatingHandler
{
    private readonly GitHubApiOptions _options;
    private readonly TimeProvider _clock;
    private readonly Random _random = new();

    public GitHubRetryHandler(GitHubApiOptions? options = null, TimeProvider? clock = null)
    {
        _options = options ?? new GitHubApiOptions();
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!IsRetryable(request))
        {
            return await base.SendAsync(request, ct).ConfigureAwait(false);
        }

        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var clone = Clone(request, body);
                response = await base.SendAsync(clone, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < _options.MaxRetries)
            {
                await DelayAsync(Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }

            var retryAfter = RetryAfter(response);
            var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500
                || (retryAfter is not null && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests);
            if (!transient || attempt >= _options.MaxRetries)
            {
                return response;
            }

            var delay = retryAfter is { } serverDelay && _options.RespectRetryAfter
                ? serverDelay
                : Backoff(attempt);
            response.Dispose();
            await DelayAsync(delay, ct).ConfigureAwait(false);
        }
    }

    private static bool IsRetryable(HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head)
        {
            return true;
        }

        if (request.Method != HttpMethod.Post)
        {
            return false;
        }

        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        return path.EndsWith("/git/blobs", StringComparison.Ordinal)
            || path.EndsWith("/git/trees", StringComparison.Ordinal)
            || path.EndsWith("/git/commits", StringComparison.Ordinal);
    }

    private static HttpRequestMessage Clone(HttpRequestMessage source, byte[]? body)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy,
        };

        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            foreach (var header in source.Content!.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private TimeSpan Backoff(int attempt)
    {
        var exponential = _options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt);
        var jitter = 0.8 + (_random.NextDouble() * 0.4);
        return TimeSpan.FromMilliseconds(Math.Min(_options.MaxDelay.TotalMilliseconds, exponential * jitter));
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            return date - DateTimeOffset.UtcNow;
        }

        return null;
    }
}
