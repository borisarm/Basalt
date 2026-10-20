using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Basalt.Application.Auth;
using Basalt.Application.Ports.Auth;
using Basalt.Application.Ports.Remote;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;

namespace Basalt.Adapters.GitHub;

/// <summary>OAuth Device Flow (RFC 8628) against github.com or GitHub Enterprise (spec 06 §9.1).</summary>
internal sealed class GitHubDeviceFlowAuthenticator : IGitHubAuthenticator
{
    private readonly HttpClient _client;
    private readonly string _clientId;
    private readonly TimeProvider _clock;

    public GitHubDeviceFlowAuthenticator(HttpClient client, string clientId)
    {
        _client = client;
        _clientId = clientId;
        _clock = TimeProvider.System;
    }

    public async Task<Result<DeviceCodeChallenge>> StartDeviceFlowAsync(GitHubHost host, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(host.WebUrl, "/login/device/code"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["scope"] = "repo read:user",
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await SendAsync<GitHubDeviceCodeDto>(request, ct).ConfigureAwait(false);
        return response.IsSuccess
            ? new DeviceCodeChallenge(
                host,
                response.Value.DeviceCode,
                response.Value.UserCode,
                response.Value.VerificationUri,
                TimeSpan.FromSeconds(Math.Max(1, response.Value.Interval)),
                _clock.GetUtcNow().AddSeconds(response.Value.ExpiresIn))
            : Result<DeviceCodeChallenge>.Failure(response.Error);
    }

    public async Task<Result<GitHubToken>> PollForTokenAsync(DeviceCodeChallenge challenge, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(challenge.Host.WebUrl, "/login/oauth/access_token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["device_code"] = challenge.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await SendAsync<GitHubAccessTokenDto>(request, ct).ConfigureAwait(false);
        return response.IsSuccess ? MapToken(response.Value) : Result<GitHubToken>.Failure(response.Error);
    }

    public async Task<Result<GitHubUserInfo>> GetUserAsync(
        GitHubHost host,
        GitHubToken token,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(host.ApiUrl, "/user"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        try
        {
            using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<GitHubUserInfo>.Failure(response.StatusCode == HttpStatusCode.Unauthorized
                    ? new Error(AuthErrors.Unauthorized, "The GitHub token is not valid.")
                    : new Error(AuthErrors.InvalidResponse, "GitHub user request failed."));
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var user = await JsonSerializer.DeserializeAsync(stream, TypeInfo<GitHubUserDto>(), ct)
                .ConfigureAwait(false);
            if (user is null)
            {
                return Result<GitHubUserInfo>.Failure(
                    new Error(AuthErrors.InvalidResponse, "Empty GitHub user response."));
            }

            var scopes = response.Headers.TryGetValues("x-oauth-scopes", out var values)
                ? values.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .ToArray()
                : [];
            return new GitHubUserInfo(
                user.Id,
                user.Login,
                user.Name,
                Uri.TryCreate(user.AvatarUrl, UriKind.Absolute, out var avatar) ? avatar : null,
                scopes);
        }
        catch (HttpRequestException exception)
        {
            return Result<GitHubUserInfo>.Failure(new Error(RemoteErrors.Network, exception.Message));
        }
    }

    public async Task<Result<GitHubToken>> RefreshAsync(GitHubHost host, GitHubToken token, CancellationToken ct)
    {
        if (token.RefreshToken is null)
        {
            return Result<GitHubToken>.Failure(
                new Error(AuthErrors.Unauthorized, "The token cannot be refreshed."));
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(host.WebUrl, "/login/oauth/access_token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = token.RefreshToken,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await SendAsync<GitHubAccessTokenDto>(request, ct).ConfigureAwait(false);
        return response.IsSuccess ? MapToken(response.Value) : Result<GitHubToken>.Failure(response.Error);
    }

    private Result<GitHubToken> MapToken(GitHubAccessTokenDto dto)
    {
        if (!string.IsNullOrEmpty(dto.Error))
        {
            var code = dto.Error switch
            {
                "authorization_pending" => AuthErrors.AuthorizationPending,
                "slow_down" => AuthErrors.SlowDown,
                "access_denied" => AuthErrors.AccessDenied,
                "expired_token" => AuthErrors.ExpiredToken,
                _ => AuthErrors.InvalidResponse,
            };
            var metadata = dto.Interval is { } interval
                ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["interval"] = interval }
                : null;
            return new Error(code, dto.ErrorDescription ?? dto.Error, metadata);
        }

        if (string.IsNullOrEmpty(dto.AccessToken))
        {
            return new Error(AuthErrors.InvalidResponse, "GitHub did not return an access token.");
        }

        return new GitHubToken(
            dto.AccessToken,
            dto.ExpiresIn is { } expires ? _clock.GetUtcNow().AddSeconds(expires) : null,
            dto.RefreshToken);
    }

    private async Task<Result<T>> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var body = await JsonSerializer.DeserializeAsync(stream, TypeInfo<T>(), ct).ConfigureAwait(false);
            if (body is null)
            {
                return Result<T>.Failure(new Error(AuthErrors.InvalidResponse, "Empty response."));
            }

            return Result<T>.Success(body);
        }
        catch (JsonException exception)
        {
            return Result<T>.Failure(new Error(AuthErrors.InvalidResponse, exception.Message));
        }
        catch (HttpRequestException exception)
        {
            return Result<T>.Failure(new Error(RemoteErrors.Network, exception.Message));
        }
    }

    private static JsonTypeInfo<T> TypeInfo<T>() =>
        (JsonTypeInfo<T>)GitHubJsonContext.Default.GetTypeInfo(typeof(T))!;
}
