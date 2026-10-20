using Basalt.Application.Abstractions;
using Basalt.Application.Indexing;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Search;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Search;

public enum QuickSwitchKind
{
    Note,
    Alias,
    Attachment,

    /// <summary>"Create {text}": no note has exactly that name (F-008.1).</summary>
    Create,
}

/// <summary>A quick switcher entry; <see cref="Positions"/> index into <see cref="Display"/>.</summary>
public sealed record QuickSwitchItem(QuickSwitchKind Kind, VaultPath? Path, string Display, IReadOnlyList<int> Positions, double Score);

/// <summary>
/// Quick switcher (F-008): fuzzy over titles, aliases and attachment names — over paths (without <c>.md</c>) when the
/// text has a <c>/</c>. "Create" comes first when no note has exactly that name. With empty text, the most recently
/// modified notes.
/// </summary>
public sealed record QuickSwitch(VaultId VaultId, string Text, int Limit = 50) : IQuery<IReadOnlyList<QuickSwitchItem>>, IVaultRequest;

public sealed class QuickSwitchHandler(IVaultSessions sessions) : IQueryHandler<QuickSwitch, IReadOnlyList<QuickSwitchItem>>
{
    public Task<Result<IReadOnlyList<QuickSwitchItem>>> HandleAsync(QuickSwitch query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return Task.FromResult(Result.Failure<IReadOnlyList<QuickSwitchItem>>(session.Error));
        }

        var files = session.Value.Get<IMetadataCache>().Current.Files.Values;
        var text = query.Text.Trim();
        if (text.Length == 0)
        {
            return Task.FromResult(Result.Success<IReadOnlyList<QuickSwitchItem>>([.. files
                .Where(f => f.Kind == FileKind.Note)
                .OrderByDescending(f => f.Stamp.LastWriteUtc)
                .ThenBy(f => f.Path.ToString(), StringComparer.Ordinal)
                .Take(query.Limit)
                .Select(f => new QuickSwitchItem(QuickSwitchKind.Note, f.Path, f.Path.NameWithoutExtension, [], 0))]));
        }

        var byPath = text.Contains('/', StringComparison.Ordinal);
        var candidates = new List<(QuickSwitchKind Kind, VaultPath Path, string Key)>();
        foreach (var file in files)
        {
            if (file.Kind == FileKind.Note)
            {
                candidates.Add((QuickSwitchKind.Note, file.Path, byPath ? WithoutExtension(file.Path) : file.Path.NameWithoutExtension));
                candidates.AddRange(file.Metadata.Aliases.Select(a => (QuickSwitchKind.Alias, file.Path, a)));
            }
            else
            {
                candidates.Add((QuickSwitchKind.Attachment, file.Path, byPath ? file.Path.ToString() : file.Path.Name));
            }
        }

        var items = new List<QuickSwitchItem>();
        var exists = candidates.Any(c => c.Kind == QuickSwitchKind.Note && string.Equals(c.Key, text, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            items.Add(new QuickSwitchItem(QuickSwitchKind.Create, null, text, [], 0));
        }

        items.AddRange(FuzzyMatcher.Rank(text, candidates, c => c.Key)
            .Take(query.Limit)
            .Select(r => new QuickSwitchItem(r.Item.Kind, r.Item.Path, r.Item.Key, r.Match.Positions, r.Match.Score)));
        return Task.FromResult(Result.Success<IReadOnlyList<QuickSwitchItem>>(items));
    }

    private static string WithoutExtension(VaultPath path)
    {
        var text = path.ToString();
        return text[..^(path.Extension.Length + 1)];
    }
}
