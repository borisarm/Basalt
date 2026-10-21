using System.Globalization;
using System.Text;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Application.Sync;

/// <summary>Builds the commit message and trailers of spec 06 §8.</summary>
public static class CommitMessageBuilder
{
    private const int MaxListedPaths = 20;

    public static string Build(
        CommitSettings settings,
        string deviceName,
        string deviceId,
        string basaltVersion,
        string vaultName,
        DateTimeOffset when,
        Manifest remote,
        MaterializedPlan plan,
        IReadOnlyList<SyncConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(conflicts);

        var changes = ChangeSet.Between(remote, plan.Target);
        var copies = conflicts
            .Where(conflict => conflict.CopyPath is not null)
            .Select(conflict => conflict.CopyPath!.Value)
            .ToHashSet();
        var summary = Summary(changes, copies, conflicts.Count);
        var subject = settings.MessageTemplate
            .Replace("{device}", deviceName, StringComparison.Ordinal)
            .Replace("{summary}", summary, StringComparison.Ordinal)
            .Replace("{date}", when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{vault}", vaultName, StringComparison.Ordinal);

        var listed = changes.Added.Select(entry => (Path: entry.Path, Kind: copies.Contains(entry.Path) ? "C" : "A"))
            .Concat(changes.Modified.Select(entry => (Path: entry.Path, Kind: "M")))
            .Concat(changes.Deleted.Select(path => (Path: path, Kind: "D")))
            .OrderBy(item => item.Path)
            .ToList();

        var body = new StringBuilder();
        foreach (var (path, kind) in listed.Take(MaxListedPaths))
        {
            body.Append(kind).Append(' ').Append(path).Append('\n');
        }

        if (listed.Count > MaxListedPaths)
        {
            body.Append("… y ").Append(listed.Count - MaxListedPaths).Append(" más\n");
        }

        if (body.Length > 0)
        {
            body.Append('\n');
        }

        body.Append("Basalt-Device: ").Append(deviceName).Append('\n');
        body.Append("Basalt-Device-Id: ").Append(deviceId).Append('\n');
        body.Append("Basalt-Version: ").Append(basaltVersion);
        return subject + "\n\n" + body;
    }

    private static string Summary(ChangeSet changes, HashSet<VaultPath> copies, int conflicts)
    {
        var parts = new List<string>();
        if (changes.Modified.Count > 0)
        {
            parts.Add(Count(changes.Modified.Count, "modificado", "modificados"));
        }

        var added = changes.Added.Count(entry => !copies.Contains(entry.Path));
        if (added > 0)
        {
            parts.Add(Count(added, "añadido", "añadidos"));
        }

        if (changes.Deleted.Count > 0)
        {
            parts.Add(Count(changes.Deleted.Count, "eliminado", "eliminados"));
        }

        if (conflicts > 0)
        {
            parts.Add(Count(conflicts, "conflicto", "conflictos"));
        }

        return parts.Count == 0 ? "sin cambios" : string.Join(", ", parts);
    }

    private static string Count(int count, string singular, string plural) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural);
}
