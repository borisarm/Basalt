using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Basalt.Application.Auth;
using Basalt.Application.Ports.Remote;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Adapters.GitHub;

internal sealed record GitHubConditional<T>(T? Value, string? ETag, bool NotModified);

internal sealed record GitHubPage<T>(IReadOnlyList<T> Items, string? Next);

/// <summary>
/// Low-level GitHub REST client: JSON source generation, ETag/304, pagination, rate-limit headers and error mapping
/// (spec 06 §9.2 and §9.3). The typed remote repository and administration adapters build on it.
/// </summary>
internal sealed class GitHubApiClient
{
    private const string ApiVersion = "2022-11-28";
    private const string JsonMediaType = "application/vnd.github+json";

    private readonly HttpClient _client;
    private readonly RemoteRepositoryRef _repository;
    private readonly TimeProvider _clock;
    private GitHubRateLimit? _lastRateLimit;

    public GitHubApiClient(HttpClient client, RemoteRepositoryRef repository, TimeProvider? clock = null)
    {
        _client = client;
        _repository = repository;
        _clock = clock ?? TimeProvider.System;
    }

    public GitHubRateLimit? LastRateLimit => Volatile.Read(ref _lastRateLimit);

    public async Task<Result<GitHubUserInfo>> GetUserAsync(CancellationToken ct)
    {
        var result = await SendJsonAsync<GitHubUserDto>(HttpMethod.Get, "/user", null, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Result<GitHubUserInfo>.Failure(result.Error);
        }

        var user = result.Value;
        return new GitHubUserInfo(
            user.Id,
            user.Login,
            user.Name,
            Uri.TryCreate(user.AvatarUrl, UriKind.Absolute, out var avatar) ? avatar : null,
            []);
    }

    public Task<Result<GitHubPage<GitHubRepositoryDto>>> ListRepositoriesAsync(
        int page,
        int perPage,
        CancellationToken ct)
    {
        var path = $"/user/repos?per_page={perPage.ToString(CultureInfo.InvariantCulture)}"
            + $"&page={page.ToString(CultureInfo.InvariantCulture)}"
            + "&sort=updated&affiliation=owner,collaborator,organization_member";
        return SendPageAsync<GitHubRepositoryDto>(path, ct);
    }

    public Task<Result<GitHubRepositoryDto>> GetRepositoryAsync(string owner, string name, CancellationToken ct) =>
        SendJsonAsync<GitHubRepositoryDto>(HttpMethod.Get, $"/repos/{Escape(owner)}/{Escape(name)}", null, ct);

    public Task<Result<GitHubRepositoryDto>> CreateRepositoryAsync(
        string? org,
        string name,
        bool isPrivate,
        CancellationToken ct)
    {
        var path = org is null ? "/user/repos" : $"/orgs/{Escape(org)}/repos";
        var request = new CreateRepositoryRequest(name, isPrivate, AutoInit: false);
        return SendJsonAsync<GitHubRepositoryDto>(HttpMethod.Post, path, request, ct);
    }

    public async Task<Result<GitHubConditional<GitHubRefDto>>> GetRefAsync(
        string? etag,
        CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, RefPath);
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        var sent = await SendRawAsync(request, ct).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return Result<GitHubConditional<GitHubRefDto>>.Failure(sent.Error);
        }

        using var response = sent.Value;
        ObserveRateLimit(response);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new GitHubConditional<GitHubRefDto>(null, response.Headers.ETag?.Tag, NotModified: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            return Result<GitHubConditional<GitHubRefDto>>.Failure(await MapErrorAsync(response, ct).ConfigureAwait(false));
        }

        var value = await DeserializeAsync<GitHubRefDto>(response, ct).ConfigureAwait(false);
        return value.IsSuccess
            ? new GitHubConditional<GitHubRefDto>(value.Value, response.Headers.ETag?.Tag, NotModified: false)
            : Result<GitHubConditional<GitHubRefDto>>.Failure(value.Error);
    }

    public Task<Result<GitHubRefDto>> CreateRefAsync(string branch, CommitSha sha, CancellationToken ct) =>
        SendJsonAsync<GitHubRefDto>(
            HttpMethod.Post,
            $"{RepositoryPath}/git/refs",
            new CreateRefRequest("refs/heads/" + branch, sha.ToString()),
            ct);

    public Task<Result<GitHubRefDto>> UpdateRefAsync(
        string branch,
        CommitSha sha,
        bool force,
        CancellationToken ct) =>
        SendJsonAsync<GitHubRefDto>(
            HttpMethod.Patch,
            $"{RepositoryPath}/git/refs/heads/{Escape(branch)}",
            new UpdateRefRequest(sha.ToString(), force),
            ct);

    public Task<Result<GitHubCommitDto>> GetCommitAsync(CommitSha sha, CancellationToken ct) =>
        SendJsonAsync<GitHubCommitDto>(
            HttpMethod.Get,
            $"{RepositoryPath}/git/commits/{sha}",
            null,
            ct);

    public Task<Result<GitHubCommitDto>> CreateCommitAsync(
        string message,
        TreeSha tree,
        IReadOnlyList<CommitSha> parents,
        CommitAuthor author,
        CancellationToken ct)
    {
        var signature = Signature(author);
        return SendJsonAsync<GitHubCommitDto>(
            HttpMethod.Post,
            $"{RepositoryPath}/git/commits",
            new CreateCommitRequest(
                message,
                tree.ToString(),
                parents.Select(parent => parent.ToString()).ToArray(),
                signature,
                signature),
            ct);
    }

    public Task<Result<GitHubTreeDto>> GetTreeAsync(TreeSha tree, bool recursive, CancellationToken ct) =>
        SendJsonAsync<GitHubTreeDto>(
            HttpMethod.Get,
            $"{RepositoryPath}/git/trees/{tree}" + (recursive ? "?recursive=1" : string.Empty),
            null,
            ct);

    public Task<Result<GitHubTreeDto>> CreateTreeAsync(
        TreeSha? baseTree,
        IReadOnlyList<TreeChange> changes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var entries = changes.Select(change => change switch
        {
            TreeChange.Upsert upsert => new CreateTreeEntryRequest(
                upsert.Path.ToString(), "100644", "blob", upsert.Sha.ToString(), null),
            TreeChange.Delete delete => new CreateTreeEntryRequest(
                delete.Path.ToString(), "100644", "blob", null, null),
            _ => throw new ArgumentException("Unknown tree change.", nameof(changes)),
        }).ToArray();
        return SendJsonAsync<GitHubTreeDto>(
            HttpMethod.Post,
            $"{RepositoryPath}/git/trees",
            new CreateTreeRequest(baseTree?.ToString(), entries),
            ct);
    }

    public async Task<Result<byte[]>> GetBlobAsync(BlobSha sha, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, $"{RepositoryPath}/git/blobs/{sha}");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
        return await SendBytesAsync(request, ct).ConfigureAwait(false);
    }

    public Task<Result<GitHubBlobDto>> CreateBlobAsync(ReadOnlyMemory<byte> content, CancellationToken ct) =>
        SendJsonAsync<GitHubBlobDto>(
            HttpMethod.Post,
            $"{RepositoryPath}/git/blobs",
            new CreateBlobRequest(Convert.ToBase64String(content.Span), "base64"),
            ct);

    public async Task<Result<byte[]>> GetFileAtAsync(VaultPath path, string reference, CancellationToken ct)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            $"{RepositoryPath}/contents/{EscapePath(path)}?ref={Escape(reference)}");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
        return await SendBytesAsync(request, ct).ConfigureAwait(false);
    }

    public Task<Result<GitHubPage<GitHubCommitListItemDto>>> GetFileHistoryAsync(
        VaultPath path,
        int max,
        CancellationToken ct)
    {
        var query = $"?path={Uri.EscapeDataString(path.ToString())}&sha={Escape(_repository.Branch)}"
            + $"&per_page={max.ToString(CultureInfo.InvariantCulture)}";
        return SendPageAsync<GitHubCommitListItemDto>($"{RepositoryPath}/commits{query}", ct);
    }

    public Task<Result<GitHubContentDto>> PutContentsAsync(
        VaultPath path,
        byte[] content,
        string message,
        string? sha,
        CancellationToken ct) =>
        SendJsonAsync<GitHubContentDto>(
            HttpMethod.Put,
            $"{RepositoryPath}/contents/{EscapePath(path)}",
            new PutContentsRequest(message, Convert.ToBase64String(content), _repository.Branch, sha),
            ct);

    public async Task<Result<byte[]>> DownloadZipballAsync(CommitSha commit, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, $"{RepositoryPath}/zipball/{commit}");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        return await SendBytesAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<Result<GitHubDeviceCodeDto>> StartDeviceFlowAsync(string clientId, string scope, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, "/login/device/code");
        request.RequestUri = new Uri(_repository.Host.WebUrl, "/login/device/code");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = scope,
        });
        return await SendAsync<GitHubDeviceCodeDto>(request, ct).ConfigureAwait(false);
    }

    public async Task<Result<GitHubAccessTokenDto>> PollDeviceTokenAsync(
        string clientId,
        string deviceCode,
        CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, "/login/oauth/access_token");
        request.RequestUri = new Uri(_repository.Host.WebUrl, "/login/oauth/access_token");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["device_code"] = deviceCode,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
        });
        return await SendAsync<GitHubAccessTokenDto>(request, ct).ConfigureAwait(false);
    }

    private string RepositoryPath =>
        $"/repos/{Escape(_repository.Owner)}/{Escape(_repository.Name)}";

    private string RefPath => $"{RepositoryPath}/git/ref/heads/{Escape(_repository.Branch)}";

    private GitHubSignatureDto Signature(CommitAuthor author) => new(
        author.Name,
        author.Email,
        _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));

    private async Task<Result<T>> SendJsonAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct)
    {
        using var request = CreateRequest(method, path);
        if (body is not null)
        {
            request.Content = Json(body);
        }

        return await SendAsync<T>(request, ct).ConfigureAwait(false);
    }

    private async Task<Result<GitHubPage<T>>> SendPageAsync<T>(string path, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        var sent = await SendRawAsync(request, ct).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return Result<GitHubPage<T>>.Failure(sent.Error);
        }

        using var response = sent.Value;
        ObserveRateLimit(response);
        if (!response.IsSuccessStatusCode)
        {
            return Result<GitHubPage<T>>.Failure(await MapErrorAsync(response, ct).ConfigureAwait(false));
        }

        var value = await DeserializeAsync<IReadOnlyList<T>>(response, ct).ConfigureAwait(false);
        return value.IsSuccess
            ? new GitHubPage<T>(value.Value, NextLink(response))
            : Result<GitHubPage<T>>.Failure(value.Error);
    }

    private async Task<Result<T>> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        var sent = await SendRawAsync(request, ct).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return Result<T>.Failure(sent.Error);
        }

        using var response = sent.Value;
        ObserveRateLimit(response);
        if (!response.IsSuccessStatusCode)
        {
            return Result<T>.Failure(await MapErrorAsync(response, ct).ConfigureAwait(false));
        }

        return await DeserializeAsync<T>(response, ct).ConfigureAwait(false);
    }

    private async Task<Result<byte[]>> SendBytesAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var sent = await SendRawAsync(request, ct).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return Result<byte[]>.Failure(sent.Error);
        }

        using var response = sent.Value;
        ObserveRateLimit(response);
        if (!response.IsSuccessStatusCode)
        {
            return Result<byte[]>.Failure(await MapErrorAsync(response, ct).ConfigureAwait(false));
        }

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private async Task<Result<HttpResponseMessage>> SendRawAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            return Result<HttpResponseMessage>.Failure(
                new Error(RemoteErrors.Network, exception.Message));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result<HttpResponseMessage>.Failure(
                new Error(RemoteErrors.Timeout, "The GitHub request timed out."));
        }
    }

    private static async Task<Result<T>> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var value = await JsonSerializer.DeserializeAsync(stream, TypeInfo<T>(), ct).ConfigureAwait(false);
            return value is null
                ? Result<T>.Failure(Error(RemoteErrors.ServerError, "Empty JSON response."))
                : Result<T>.Success(value);
        }
        catch (JsonException exception)
        {
            return Result<T>.Failure(Error(RemoteErrors.ServerError, "Invalid JSON: " + exception.Message));
        }
    }

    private async Task<Error> MapErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var message = body;
        try
        {
            var error = JsonSerializer.Deserialize(body, TypeInfo<GitHubErrorDto>());
            if (!string.IsNullOrWhiteSpace(error?.Message))
            {
                message = error.Message;
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body: keep the raw text as the log message.
        }

        var retryAt = RetryAt(response);
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = (int)response.StatusCode,
        };
        if (retryAt is { } reset)
        {
            metadata["retry_at"] = reset;
        }

        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => RemoteErrors.Unauthorized,
            HttpStatusCode.Forbidden when LastRateLimit is { Remaining: <= 0 } || response.Headers.RetryAfter is not null =>
                RemoteErrors.RateLimited,
            HttpStatusCode.Forbidden => RemoteErrors.Forbidden,
            HttpStatusCode.NotFound => RemoteErrors.NotFound,
            HttpStatusCode.Conflict when message.Contains("empty", StringComparison.OrdinalIgnoreCase) =>
                RemoteErrors.RepositoryEmpty,
            HttpStatusCode.Conflict => RemoteErrors.Conflict,
            HttpStatusCode.TooManyRequests => RemoteErrors.RateLimited,
            HttpStatusCode.UnprocessableEntity when message.Contains("fast forward", StringComparison.OrdinalIgnoreCase) =>
                RemoteErrors.NonFastForward,
            HttpStatusCode.UnprocessableEntity => RemoteErrors.ValidationFailed,
            _ when (int)response.StatusCode >= 500 => RemoteErrors.ServerError,
            _ => RemoteErrors.ValidationFailed,
        };
        return new Error(code, string.IsNullOrWhiteSpace(message) ? response.ReasonPhrase ?? code : message, metadata);
    }

    private void ObserveRateLimit(HttpResponseMessage response)
    {
        if (!TryInt(response, "x-ratelimit-limit", out var limit)
            || !TryInt(response, "x-ratelimit-remaining", out var remaining)
            || !TryInt(response, "x-ratelimit-reset", out var reset))
        {
            return;
        }

        TryInt(response, "x-ratelimit-used", out var used);
        Volatile.Write(ref _lastRateLimit, new GitHubRateLimit(
            limit,
            remaining,
            used,
            DateTimeOffset.FromUnixTimeSeconds(reset)));
    }

    private static bool TryInt(HttpResponseMessage response, string name, out int value)
    {
        value = 0;
        return response.Headers.TryGetValues(name, out var values)
            && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static DateTimeOffset? RetryAt(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return DateTimeOffset.UtcNow + delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            return date;
        }

        return response.Headers.TryGetValues("x-ratelimit-reset", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var reset)
                ? DateTimeOffset.FromUnixTimeSeconds(reset)
                : null;
    }

    private static string? NextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        foreach (var part in string.Join(',', values).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!part.Contains("rel=\"next\"", StringComparison.Ordinal))
            {
                continue;
            }

            var start = part.IndexOf('<');
            var end = part.IndexOf('>');
            if (start >= 0 && end > start)
            {
                return part[(start + 1)..end];
            }
        }

        return null;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.ParseAdd(JsonMediaType);
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        return request;
    }

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value, TypeInfo(value.GetType())), Encoding.UTF8, "application/json");

    private static JsonTypeInfo TypeInfo(Type type) =>
        GitHubJsonContext.Default.GetTypeInfo(type)
        ?? throw new InvalidOperationException($"No JSON metadata for {type}.");

    private static JsonTypeInfo<T> TypeInfo<T>() =>
        (JsonTypeInfo<T>)GitHubJsonContext.Default.GetTypeInfo(typeof(T))!;

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string EscapePath(VaultPath path) =>
        string.Join('/', path.Segments.Select(Uri.EscapeDataString));

    private static Error Error(string code, string message) => new(code, message);
}
