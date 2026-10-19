using Basalt.Domain.Common;

namespace Basalt.Domain.Accounts;

/// <summary>Local identifier of a GitHub account registered on this device (spec 02 §12).</summary>
public readonly record struct GitHubAccountId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// A GitHub or GitHub Enterprise Server host: <c>https://github.com</c> (API <c>https://api.github.com</c>) or
/// <c>https://{host}</c> (API <c>https://{host}/api/v3</c>).
/// </summary>
public sealed record GitHubHost
{
    private GitHubHost(Uri webUrl, Uri apiUrl)
    {
        WebUrl = webUrl;
        ApiUrl = apiUrl;
    }

    public static GitHubHost GitHubDotCom { get; } = new(new Uri("https://github.com"), new Uri("https://api.github.com"));

    public Uri WebUrl { get; }

    public Uri ApiUrl { get; }

    public bool IsGitHubDotCom => WebUrl == GitHubDotCom.WebUrl;

    /// <returns><c>account.invalid_host</c> unless the URL is an absolute https URL without path.</returns>
    public static Result<GitHubHost> Create(string webUrl)
    {
        ArgumentNullException.ThrowIfNull(webUrl);
        if (!Uri.TryCreate(webUrl.TrimEnd('/'), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/")
        {
            return new Error(DomainErrors.AccountInvalidHost, "GitHub hosts are https URLs without a path.");
        }

        return string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            ? GitHubDotCom
            : new GitHubHost(uri, new Uri(uri, "/api/v3/"));
    }

    /// <summary>For tests and fakes: an http(s) host whose API lives at its root (e.g. a local fake server).</summary>
    public static GitHubHost ForTesting(Uri baseUrl) => new(baseUrl, baseUrl);

    public override string ToString() => WebUrl.ToString().TrimEnd('/');
}

/// <summary>A GitHub account. The token is not part of the entity (it lives in the secure store).</summary>
public sealed record GitHubAccount(
    GitHubAccountId Id,
    string Login,
    GitHubHost Host,
    Uri? AvatarUrl,
    IReadOnlyList<string> Scopes,
    DateTimeOffset AddedAt)
{
    /// <summary>Commit e-mail used when the user did not configure one (spec 06 §8).</summary>
    public string NoReplyEmail(long userId) => $"{userId}+{Login}@users.noreply.github.com";
}
