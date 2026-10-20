using Basalt.Application.Ports.Platform;
using Microsoft.Extensions.Time.Testing;

namespace Basalt.Testing.Fakes;

/// <summary>Advanceable clock over <see cref="FakeTimeProvider"/>.</summary>
public sealed class FakeClock(FakeTimeProvider time, TimeZoneInfo? zone = null) : IClock
{
    public FakeClock()
        : this(new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 15, 42, 33, TimeSpan.Zero)))
    {
    }

    public FakeTimeProvider Time { get; } = time;

    public DateTimeOffset UtcNow => Time.GetUtcNow();

    public TimeZoneInfo LocalZone { get; } = zone ?? TimeZoneInfo.Utc;

    public void Advance(TimeSpan delta) => Time.Advance(delta);
}

/// <summary>Deterministic GUIDs: 00000000-0000-0000-0000-000000000001, ...002, ...</summary>
public sealed class SequentialIdGenerator : IIdGenerator
{
    private int _next;

    public Guid NewId() => new($"00000000-0000-0000-0000-{Interlocked.Increment(ref _next):D12}");
}

/// <summary>Seeded randomness.</summary>
#pragma warning disable CA5394 // deterministic test randomness
public sealed class FakeRandom(int seed = A.Seed) : IRandom
{
    private readonly Random _random = new(seed);

    public int Next(int maxExclusive) => _random.Next(maxExclusive);

    public double NextDouble() => _random.NextDouble();
}
#pragma warning restore CA5394

/// <summary>A fixed device.</summary>
public sealed class FakeDeviceInfo(string displayName = "Test Laptop") : IDeviceInfo
{
    public Guid DeviceId { get; } = new("0000000d-0000-0000-0000-000000000001");

    public string DisplayName { get; } = displayName;
}
