using Basalt.Application.Ports.Platform;

namespace Basalt.Adapters.Platform;

/// <summary>
/// Desktop locations (spec 03 §1.6): <c>%LOCALAPPDATA%\Basalt</c> on Windows, <c>~/Library/Application Support/Basalt</c>
/// on macOS. Vaults default to <c>Documents/Basalt</c>.
/// </summary>
internal sealed class DesktopPlatformPaths : IPlatformPaths
{
    public DesktopPlatformPaths(string? appDataOverride = null)
    {
        AppDataDirectory = appDataOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Basalt");
        DefaultVaultsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Basalt");
        LogsDirectory = Path.Combine(AppDataDirectory, "logs");
    }

    public string AppDataDirectory { get; }

    public string DefaultVaultsDirectory { get; }

    public string LogsDirectory { get; }
}
