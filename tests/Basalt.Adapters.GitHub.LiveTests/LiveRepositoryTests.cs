using Basalt.Application.Auth;
using Basalt.Application.Ports.Remote;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Basalt.Adapters.GitHub.LiveTests;

/// <summary>Optional live tests (spec 09 §2). Set BASALT_GITHUB_TOKEN, OWNER and REPO to run them.</summary>
public sealed class LiveRepositoryTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task RepositoryInfo_IsReadableWithARealToken()
    {
        var token = Environment.GetEnvironmentVariable("BASALT_GITHUB_TOKEN");
        var owner = Environment.GetEnvironmentVariable("BASALT_GITHUB_OWNER");
        var name = Environment.GetEnvironmentVariable("BASALT_GITHUB_REPO");
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(name))
        {
            Assert.Skip("Live GitHub tests need BASALT_GITHUB_TOKEN, BASALT_GITHUB_OWNER and BASALT_GITHUB_REPO.");
        }

        var services = new ServiceCollection().AddLogging();
        services.TryAddSingleton<IGitHubTokenProvider>(new StaticTokenProvider(token));
        services.AddBasaltGitHubAdapter(GitHubHost.GitHubDotCom);
        await using var provider = services.BuildServiceProvider();

        var repository = await provider.GetRequiredService<IRepositoryAdministration>()
            .GetRepositoryAsync(new GitHubAccountId(Guid.NewGuid()), owner, name, TestContext.Current.CancellationToken);

        repository.IsSuccess.Should().BeTrue();
        repository.Value.FullName.Should().Be(owner + "/" + name);
    }

    private sealed class StaticTokenProvider(string token) : IGitHubTokenProvider
    {
        public Task<Result<GitHubToken>> GetTokenAsync(GitHubAccountId account, CancellationToken ct) =>
            Task.FromResult(Result<GitHubToken>.Success(new GitHubToken(token)));
    }
}
