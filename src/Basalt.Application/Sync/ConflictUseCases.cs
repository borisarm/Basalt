using System.Text.RegularExpressions;
using Basalt.Application.Abstractions;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Sync;

/// <summary>A conflict copy or a note that still contains merge markers (spec 06 §5 and §12).</summary>
public sealed record SyncConflictEntry(VaultPath Path, bool IsConflictCopy, bool HasMarkers);

public sealed record ListConflicts(VaultId VaultId) : IQuery<IReadOnlyList<SyncConflictEntry>>, IVaultRequest;

public sealed partial class ListConflictsHandler(IVaultSessions sessions)
    : IQueryHandler<ListConflicts, IReadOnlyList<SyncConflictEntry>>
{
    public async Task<Result<IReadOnlyList<SyncConflictEntry>>> HandleAsync(ListConflicts query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var conflicts = new List<SyncConflictEntry>();
        await foreach (var entry in session.Value.FileSystem
            .EnumerateAsync(VaultPath.Root, recursive: true, ct)
            .ConfigureAwait(false))
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            var copy = ConflictCopyPattern().IsMatch(entry.Path.Name);
            var markers = !copy
                && string.Equals(Path.GetExtension(entry.Path.Name), ".md", StringComparison.OrdinalIgnoreCase)
                && HasMarkers(await session.Value.FileSystem.ReadAllTextAsync(entry.Path, ct).ConfigureAwait(false));
            if (copy || markers)
            {
                conflicts.Add(new SyncConflictEntry(entry.Path, copy, markers));
            }
        }

        return conflicts.OrderBy(conflict => conflict.Path).ToList();
    }

    private static bool HasMarkers(Result<string> text) =>
        text.IsSuccess
        && (text.Value.StartsWith("<<<<<<< ", StringComparison.Ordinal)
            || text.Value.Contains("\n<<<<<<< ", StringComparison.Ordinal))
        && text.Value.Contains("\n>>>>>>> ", StringComparison.Ordinal);

    [GeneratedRegex(@" \(conflict [^)]+\)(\.[^.]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ConflictCopyPattern();
}
