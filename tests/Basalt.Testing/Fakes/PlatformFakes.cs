using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
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

/// <summary>A fixed culture (es-ES by default, the culture of the spec examples).</summary>
public sealed class FakeCultureProvider(string name = "es-ES") : ICultureProvider
{
    public System.Globalization.CultureInfo Current { get; } = System.Globalization.CultureInfo.GetCultureInfo(name);
}

/// <summary>A recycle bin over an in-memory vault: trashed entries are deleted from the vault and remembered.</summary>
public sealed class FakeSystemTrash(IVaultFileSystem vault, bool available = true) : ISystemTrash
{
    private readonly List<VaultPath> _trashed = [];

    public bool IsAvailable => available;

    public IReadOnlyList<VaultPath> Trashed => _trashed;

    public async Task<Result<Unit>> MoveToTrashAsync(string rootLocation, VaultPath path, CancellationToken ct)
    {
        if (!available)
        {
            return new Error(ErrorCodes.FsTrashUnavailable, "This system has no recycle bin.");
        }

        var deleted = await vault.DeleteAsync(path, ct);
        if (deleted.IsSuccess)
        {
            _trashed.Add(path);
        }

        return deleted;
    }
}
