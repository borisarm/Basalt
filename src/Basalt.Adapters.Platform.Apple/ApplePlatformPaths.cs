using Basalt.Application.Ports.Platform;

namespace Basalt.Adapters.Platform.Apple;

/// <summary>
/// Apple locations (spec 03 §1.6 and 12 §3): application data in <c>Library/Application Support/Basalt</c> and vaults in
/// <c>Documents/Basalt</c>, visible in the Files app on iOS (<c>UIFileSharingEnabled</c>).
/// </summary>
internal sealed class ApplePlatformPaths : IPlatformPaths
{
    public ApplePlatformPaths(string? appDataOverride = null)
    {
        AppDataDirectory = appDataOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Basalt");
        DefaultVaultsDirectory = Path.Combine(DocumentsFolder(), "Basalt");
        LogsDirectory = Path.Combine(AppDataDirectory, "logs");
    }

    public string AppDataDirectory { get; }

    public string DefaultVaultsDirectory { get; }

    public string LogsDirectory { get; }

    private static string DocumentsFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (Path.IsPathRooted(documents))
        {
            return documents;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.IsPathRooted(home) ? home : Path.GetTempPath();
    }
}
