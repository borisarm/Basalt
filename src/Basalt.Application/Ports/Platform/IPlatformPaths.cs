namespace Basalt.Application.Ports.Platform;

/// <summary>Well-known locations of the device (spec 03 §1.7, 08 §1).</summary>
public interface IPlatformPaths
{
    /// <summary><c>%LOCALAPPDATA%\Basalt</c>, <c>~/Library/Application Support/Basalt</c> or the iOS container equivalent.</summary>
    string AppDataDirectory { get; }

    string DefaultVaultsDirectory { get; }

    string LogsDirectory { get; }
}
