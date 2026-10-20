using Basalt.Domain.Sync;

namespace Basalt.Testing.FakeGitHub;

/// <summary>Configuration of the fake GitHub server (spec 06 §14).</summary>
public sealed class FakeGitHubOptions
{
    public string Login { get; set; } = "basalt-test";

    public long UserId { get; set; } = 42;

    public string UserName { get; set; } = "Basalt Test";

    public string AvatarUrl { get; set; } = "https://avatars.example/basalt.png";

    public string Token { get; set; } = "test-token";

    public bool RequireAuthentication { get; set; } = true;

    public int RateLimit { get; set; } = 5000;

    public DateTimeOffset RateLimitReset { get; set; } = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    public int TreeTruncationThreshold { get; set; } = 100_000;

    public int PageSize { get; set; } = 30;

    public int PageSizeMax { get; set; } = 100;

    public GitSignature DefaultSignature { get; set; } =
        new("Basalt Test", "test@basalt.local", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
}
