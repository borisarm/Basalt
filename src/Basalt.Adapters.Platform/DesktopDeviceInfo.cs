using System.Text.Json;
using Basalt.Adapters.Platform.Json;
using Basalt.Application.Ports.Platform;

namespace Basalt.Adapters.Platform;

/// <summary>
/// This device's identity in <c>{AppData}/device.json</c> (spec 08 §1): generated once, then stable; the name defaults to
/// the machine name and is shown in sync history and conflict copies.
/// </summary>
internal sealed class DesktopDeviceInfo : IDeviceInfo
{
    private readonly Lazy<DeviceDocument> _document;

    public DesktopDeviceInfo(IPlatformPaths paths, IIdGenerator ids)
    {
        _document = new Lazy<DeviceDocument>(() => LoadOrCreate(Path.Combine(paths.AppDataDirectory, "device.json"), ids));
    }

    public Guid DeviceId => _document.Value.DeviceId;

    public string DisplayName => _document.Value.DisplayName;

    private static DeviceDocument LoadOrCreate(string path, IIdGenerator ids)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), PlatformJsonContext.Default.DeviceDocument) is { DeviceId: var id } existing && id != Guid.Empty)
            {
                return existing;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable file is replaced: the identity only has to be stable from now on.
        }

        var created = new DeviceDocument(ids.NewId(), Environment.MachineName);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(created, PlatformJsonContext.Default.DeviceDocument));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not persisted this time; a new identity is created on the next start.
        }

        return created;
    }
}

/// <summary>JSON shape of <c>device.json</c>.</summary>
internal sealed record DeviceDocument(Guid DeviceId, string DisplayName);
