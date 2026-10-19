using Basalt.Domain.Accounts;
using Basalt.Domain.Common;

namespace Basalt.Domain.Tests.Accounts;

public sealed class GitHubAccountTests
{
    [Theory]
    [InlineData("https://github.com")]
    [InlineData("https://GITHUB.com/")]
    public void Host_GitHubDotCom_UsesApiSubdomain(string url)
    {
        var host = GitHubHost.Create(url).Value;

        host.Should().BeSameAs(GitHubHost.GitHubDotCom);
        host.ApiUrl.Should().Be(new Uri("https://api.github.com"));
        host.IsGitHubDotCom.Should().BeTrue();
        host.ToString().Should().Be("https://github.com");
    }

    [Fact]
    public void Host_Enterprise_UsesApiV3()
    {
        var host = GitHubHost.Create("https://git.example.com").Value;

        host.ApiUrl.Should().Be(new Uri("https://git.example.com/api/v3/"));
        host.IsGitHubDotCom.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://github.com")]
    [InlineData("github.com")]
    [InlineData("https://git.example.com/path")]
    [InlineData("")]
    public void Host_Invalid(string url)
    {
        GitHubHost.Create(url).Error.Code.Should().Be(DomainErrors.AccountInvalidHost);
        GitHubHost.Create(url).Error.Message.Should().Be("GitHub hosts are https URLs without a path.");
    }

    [Fact]
    public void Account_NoReplyEmail_AndIds()
    {
        var id = new GitHubAccountId(new Guid("3f2c8a1e-0000-4000-8000-000000000001"));
        var account = new GitHubAccount(id, "boris", GitHubHost.GitHubDotCom, null, ["repo"], DateTimeOffset.UnixEpoch);

        account.NoReplyEmail(42).Should().Be("42+boris@users.noreply.github.com");
        id.ToString().Should().Be("3f2c8a1e-0000-4000-8000-000000000001");
        GitHubHost.ForTesting(new Uri("http://127.0.0.1:5000/")).ApiUrl.Should().Be(new Uri("http://127.0.0.1:5000/"));
        FluentActions.Invoking(() => GitHubHost.Create(null!)).Should().Throw<ArgumentNullException>();
    }
}
