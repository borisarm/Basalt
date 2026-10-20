namespace Basalt.Testing.FakeGitHub;

public enum FakeDeviceFlowOutcome
{
    Approve,
    Deny,
    Expire,
}

/// <summary>Configurable OAuth device flow (spec 06 §9.1 and §14).</summary>
public sealed class FakeDeviceFlow
{
    private int _polls;

    public string DeviceCode { get; set; } = "device-code";

    public string UserCode { get; set; } = "ABCD-1234";

    public string VerificationUri { get; set; } = "https://github.com/login/device";

    public int IntervalSeconds { get; set; } = 5;

    public int ExpiresInSeconds { get; set; } = 900;

    public int ApproveAfterPolls { get; set; } = 1;

    public FakeDeviceFlowOutcome Outcome { get; set; } = FakeDeviceFlowOutcome.Approve;

    /// <summary>Poll numbers (1-based) that answer <c>slow_down</c> before the final outcome.</summary>
    public HashSet<int> SlowDownPolls { get; } = [];

    public string AccessToken { get; set; } = "device-access-token";

    public int Polls => Volatile.Read(ref _polls);

    public void Reset()
    {
        Volatile.Write(ref _polls, 0);
    }

    internal DeviceFlowAnswer Poll()
    {
        var poll = Interlocked.Increment(ref _polls);
        if (SlowDownPolls.Contains(poll))
        {
            return new DeviceFlowAnswer("slow_down", null, IntervalSeconds + 5, null);
        }

        if (Outcome == FakeDeviceFlowOutcome.Deny)
        {
            return new DeviceFlowAnswer("access_denied", null, null, null);
        }

        if (Outcome == FakeDeviceFlowOutcome.Expire)
        {
            return new DeviceFlowAnswer("expired_token", null, null, null);
        }

        return poll >= ApproveAfterPolls
            ? new DeviceFlowAnswer(null, AccessToken, null, "repo,read:user")
            : new DeviceFlowAnswer("authorization_pending", null, IntervalSeconds, null);
    }
}

internal sealed record DeviceFlowAnswer(string? Error, string? Token, int? Interval, string? Scope);
