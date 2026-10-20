using System.Text.Json.Serialization;

namespace Basalt.Adapters.GitHub;

internal sealed record GitHubUserDto(
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl);

internal sealed record GitHubOwnerDto(
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("id")] long Id);

internal sealed record GitHubPermissionsDto(
    [property: JsonPropertyName("push")] bool Push,
    [property: JsonPropertyName("pull")] bool Pull);

internal sealed record GitHubRepositoryDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("full_name")] string FullName,
    [property: JsonPropertyName("private")] bool Private,
    [property: JsonPropertyName("default_branch")] string DefaultBranch,
    [property: JsonPropertyName("archived")] bool Archived,
    [property: JsonPropertyName("owner")] GitHubOwnerDto? Owner,
    [property: JsonPropertyName("permissions")] GitHubPermissionsDto? Permissions);

internal sealed record GitHubRefObjectDto(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("type")] string? Type);

internal sealed record GitHubRefDto(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("object")] GitHubRefObjectDto Object);

internal sealed record GitHubSignatureDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("date")] string? Date);

internal sealed record GitHubCommitTreeDto([property: JsonPropertyName("sha")] string Sha);

internal sealed record GitHubCommitParentDto([property: JsonPropertyName("sha")] string Sha);

internal sealed record GitHubCommitDto(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("tree")] GitHubCommitTreeDto Tree,
    [property: JsonPropertyName("parents")] IReadOnlyList<GitHubCommitParentDto>? Parents,
    [property: JsonPropertyName("author")] GitHubSignatureDto? Author,
    [property: JsonPropertyName("committer")] GitHubSignatureDto? Committer);

internal sealed record GitHubCommitDetailsDto(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("author")] GitHubSignatureDto? Author,
    [property: JsonPropertyName("committer")] GitHubSignatureDto? Committer);

internal sealed record GitHubCommitListItemDto(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("commit")] GitHubCommitDetailsDto Commit);

internal sealed record GitHubTreeEntryDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("size")] long? Size);

internal sealed record GitHubTreeDto(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("tree")] IReadOnlyList<GitHubTreeEntryDto> Tree);

internal sealed record GitHubBlobDto(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("encoding")] string? Encoding,
    [property: JsonPropertyName("size")] long Size);

internal sealed record GitHubContentDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("encoding")] string? Encoding);

internal sealed record GitHubErrorDto(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("documentation_url")] string? DocumentationUrl);

internal sealed record GitHubDeviceCodeDto(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("verification_uri")] string VerificationUri,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval);

internal sealed record GitHubAccessTokenDto(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription,
    [property: JsonPropertyName("interval")] int? Interval,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn);

internal sealed record CreateBlobRequest(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("encoding")] string Encoding);

internal sealed record CreateTreeEntryRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("sha")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? Sha,
    [property: JsonPropertyName("content")] string? Content);

internal sealed record CreateTreeRequest(
    [property: JsonPropertyName("base_tree")] string? BaseTree,
    [property: JsonPropertyName("tree")] IReadOnlyList<CreateTreeEntryRequest> Tree);

internal sealed record CreateCommitRequest(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("tree")] string Tree,
    [property: JsonPropertyName("parents")] IReadOnlyList<string> Parents,
    [property: JsonPropertyName("author")] GitHubSignatureDto Author,
    [property: JsonPropertyName("committer")] GitHubSignatureDto Committer);

internal sealed record CreateRefRequest(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("sha")] string Sha);

internal sealed record UpdateRefRequest(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("force")] bool Force);

internal sealed record CreateRepositoryRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("private")] bool Private,
    [property: JsonPropertyName("auto_init")] bool AutoInit);

internal sealed record PutContentsRequest(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("sha")] string? Sha);

internal sealed record DeviceCodeRequest(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("scope")] string Scope);

internal sealed record AccessTokenRequest(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("grant_type")] string GrantType);

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GitHubUserDto))]
[JsonSerializable(typeof(GitHubRepositoryDto))]
[JsonSerializable(typeof(IReadOnlyList<GitHubRepositoryDto>))]
[JsonSerializable(typeof(GitHubRefDto))]
[JsonSerializable(typeof(GitHubCommitDto))]
[JsonSerializable(typeof(GitHubTreeDto))]
[JsonSerializable(typeof(GitHubBlobDto))]
[JsonSerializable(typeof(GitHubContentDto))]
[JsonSerializable(typeof(IReadOnlyList<GitHubCommitDto>))]
[JsonSerializable(typeof(IReadOnlyList<GitHubCommitListItemDto>))]
[JsonSerializable(typeof(GitHubErrorDto))]
[JsonSerializable(typeof(GitHubDeviceCodeDto))]
[JsonSerializable(typeof(GitHubAccessTokenDto))]
[JsonSerializable(typeof(CreateBlobRequest))]
[JsonSerializable(typeof(CreateTreeRequest))]
[JsonSerializable(typeof(CreateCommitRequest))]
[JsonSerializable(typeof(CreateRefRequest))]
[JsonSerializable(typeof(UpdateRefRequest))]
[JsonSerializable(typeof(CreateRepositoryRequest))]
[JsonSerializable(typeof(PutContentsRequest))]
[JsonSerializable(typeof(DeviceCodeRequest))]
[JsonSerializable(typeof(AccessTokenRequest))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
