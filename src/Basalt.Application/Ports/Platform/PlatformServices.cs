namespace Basalt.Application.Ports.Platform;

/// <summary>Current time (spec 03 §1.7). The core never reads the system clock directly.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    TimeZoneInfo LocalZone { get; }
}

/// <summary>New identifiers (vault ids, account ids...).</summary>
public interface IIdGenerator
{
    Guid NewId();
}

/// <summary>Randomness (block ids, layout seeds).</summary>
public interface IRandom
{
    int Next(int maxExclusive);

    double NextDouble();
}

/// <summary>This device (spec 03 §1.7).</summary>
public interface IDeviceInfo
{
    Guid DeviceId { get; }

    string DisplayName { get; }
}
