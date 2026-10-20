using Basalt.Application.Auth;
using Basalt.Application.Ports.Auth;
using Basalt.Domain.Accounts;

namespace Basalt.Testing.Contracts;

public enum AuthenticatorScenario
{
    Pending,
    SlowDown,
    Approved,
    Denied,
    Expired,
}

/// <summary>Behaviour of <see cref="IGitHubAuthenticator"/> (spec 03 §1.5, 06 §9.1).</summary>
public abstract class AuthenticatorContract : ContractTest<IGitHubAuthenticator>
{
    protected abstract GitHubHost Host { get; }

    protected abstract string Login { get; }

    protected abstract GitHubToken ValidToken { get; }

    protected abstract GitHubToken RefreshableToken { get; }

    protected abstract Task ConfigureAsync(AuthenticatorScenario scenario);

    [Fact]
    [Trait("Spec", "AUTH-DEVICE")]
    public async Task Start_ReturnsAChallengeWithIntervalAndExpiry()
    {
        var challenge = await Sut.StartDeviceFlowAsync(Host, Ct);

        challenge.IsSuccess.Should().BeTrue();
        challenge.Value.DeviceCode.Should().NotBeNullOrEmpty();
        challenge.Value.UserCode.Should().NotBeNullOrEmpty();
        challenge.Value.Interval.Should().BeGreaterThan(TimeSpan.Zero);
        challenge.Value.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData(AuthenticatorScenario.Pending, AuthErrors.AuthorizationPending)]
    [InlineData(AuthenticatorScenario.SlowDown, AuthErrors.SlowDown)]
    [InlineData(AuthenticatorScenario.Denied, AuthErrors.AccessDenied)]
    [InlineData(AuthenticatorScenario.Expired, AuthErrors.ExpiredToken)]
    [Trait("Spec", "AUTH-DEVICE")]
    public async Task Poll_MapsTheDeviceFlowStates(AuthenticatorScenario scenario, string code)
    {
        await ConfigureAsync(scenario);
        var challenge = (await Sut.StartDeviceFlowAsync(Host, Ct)).Value;

        var poll = await Sut.PollForTokenAsync(challenge, Ct);

        poll.Error.Code.Should().Be(code);
        if (scenario == AuthenticatorScenario.SlowDown)
        {
            poll.Error.Metadata!["interval"].Should().Be(10);
        }
    }

    [Fact]
    [Trait("Spec", "AUTH-DEVICE")]
    public async Task Poll_Approved_ReturnsAToken()
    {
        await ConfigureAsync(AuthenticatorScenario.Approved);
        var challenge = (await Sut.StartDeviceFlowAsync(Host, Ct)).Value;

        var poll = await Sut.PollForTokenAsync(challenge, Ct);

        poll.IsSuccess.Should().BeTrue();
        poll.Value.Value.Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Spec", "AUTH-DEVICE")]
    public async Task GetUser_MapsProfileAndRejectsInvalidTokens()
    {
        var user = await Sut.GetUserAsync(Host, ValidToken, Ct);
        var invalid = await Sut.GetUserAsync(Host, new GitHubToken("invalid"), Ct);

        user.Value.Login.Should().Be(Login);
        user.Value.Id.Should().BePositive();
        invalid.Error.Code.Should().Be(AuthErrors.Unauthorized);
    }

    [Fact]
    [Trait("Spec", "AUTH-DEVICE")]
    public async Task Refresh_ReturnsANewToken()
    {
        var refreshed = await Sut.RefreshAsync(Host, RefreshableToken, Ct);

        refreshed.IsSuccess.Should().BeTrue();
        refreshed.Value.Value.Should().NotBeNullOrEmpty();
    }
}
