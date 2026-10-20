using System.Globalization;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;

namespace Basalt.Domain.Sync;

/// <summary>
/// Names conflict copies as <c>Nombre (conflict yyyy-MM-dd HHmmss Dispositivo).ext</c> (spec 02 §11 and 06 §5),
/// adding <c> 2</c>, <c> 3</c>… before the closing parenthesis when the name is taken.
/// </summary>
public static class ConflictCopyNamer
{
    private const int MaxAttempts = 10_000;
    private const string FallbackDevice = "device";

    public static Result<VaultPath> Name(
        VaultPath path,
        IEnumerable<VaultPath> existing,
        DateTimeOffset when,
        string deviceName)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(deviceName);
        if (path.IsRoot)
        {
            throw new ArgumentException("The vault root cannot be a conflict copy.", nameof(path));
        }

        var taken = existing.Select(candidate => candidate.CaseFoldKey).ToHashSet(StringComparer.Ordinal);
        var device = DeviceName(deviceName);
        var (stem, extension) = Split(path.Name);
        var stamp = when.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);

        for (var suffix = 1; suffix <= MaxAttempts; suffix++)
        {
            var number = suffix == 1 ? string.Empty : " " + suffix.ToString(CultureInfo.InvariantCulture);
            var name = $"{stem} (conflict {stamp} {device}{number}){extension}";
            var candidate = path.WithName(name);
            if (candidate.IsFailure)
            {
                return candidate.Error;
            }

            if (!taken.Contains(candidate.Value.CaseFoldKey))
            {
                return candidate;
            }
        }

        return new Error(DomainErrors.PathInvalidSegment, "There is no free name for the conflict copy.")
            .With("path", path.ToString());
    }

    private static string DeviceName(string device)
    {
        var sanitized = new string(device
            .Select(c => c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || c < ' ' ? '-' : c)
            .ToArray()).Trim();
        return sanitized.Length == 0 || sanitized.All(c => c is '-' or ' ') ? FallbackDevice : sanitized;
    }

    private static (string Stem, string Extension) Split(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);
    }
}
