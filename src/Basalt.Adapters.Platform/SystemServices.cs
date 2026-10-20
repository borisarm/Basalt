using Basalt.Application.Ports.Platform;

namespace Basalt.Adapters.Platform;

internal sealed class SystemClock(TimeProvider time) : IClock
{
    public DateTimeOffset UtcNow => time.GetUtcNow();

    public TimeZoneInfo LocalZone => time.LocalTimeZone;
}

internal sealed class GuidIdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.NewGuid();
}

internal sealed class SystemRandom : IRandom
{
    public int Next(int maxExclusive) => Random.Shared.Next(maxExclusive);

    public double NextDouble() => Random.Shared.NextDouble();
}
