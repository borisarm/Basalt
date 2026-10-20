using Basalt.Application.Errors;
using Basalt.Application.Indexing;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.Markdown;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Links;
using Basalt.Domain.Notes;

namespace Basalt.Application.Notes;

/// <summary>A completed move and the notes whose links were rewritten.</summary>
public sealed record MoveOutcome(IReadOnlyList<(VaultPath From, VaultPath To)> Moves, IReadOnlyList<VaultPath> RewrittenNotes);

/// <summary>
/// Moves/renames entries and, when <c>AutoUpdateLinks</c> is on, rewrites the links of every note that could be
/// affected (spec 02 §5.2, F-004.4–F-004.7) — in one operation per vault: if any step fails, rewritten notes are
/// restored and moved entries are moved back.
/// </summary>
public sealed class EntryMover(IMarkdownAnalyzer analyzer, IEventBus events)
{
    public async Task<Result<MoveOutcome>> MoveAsync(VaultSession session, IReadOnlyList<(VaultPath From, VaultPath To)> moves, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(moves);
        var fs = session.FileSystem;
        foreach (var (from, to) in moves)
        {
            if (!await fs.ExistsAsync(from, ct).ConfigureAwait(false))
            {
                return NoteFiles.NotFound(from);
            }

            if (to.IsDescendantOf(from))
            {
                return new Error(ErrorCodes.MoveIntoItself, "A folder cannot be moved into itself.");
            }

            var caseOnly = from.CaseFoldKey == to.CaseFoldKey && !fs.IsCaseSensitive;
            if (!caseOnly && await fs.ExistsAsync(to, ct).ConfigureAwait(false))
            {
                return NoteFiles.AlreadyExists(to);
            }
        }

        var before = session.TryGet<IMetadataCache>()?.Current;
        var fileMap = FileMap(before, moves);

        var done = new List<(VaultPath From, VaultPath To)>();
        foreach (var move in moves)
        {
            var moved = await fs.MoveAsync(move.From, move.To, ct).ConfigureAwait(false);
            if (moved.IsFailure)
            {
                await UndoMovesAsync(session, done, ct).ConfigureAwait(false);
                return moved.Error.Code == ErrorCodes.FsAlreadyExists ? NoteFiles.AlreadyExists(move.To) : moved.Error;
            }

            done.Add(move);
        }

        var rewritten = new List<NoteFile>();
        if (before is not null && session.Settings.AutoUpdateLinks)
        {
            var result = await RewriteLinksAsync(session, before, fileMap, rewritten, ct).ConfigureAwait(false);
            if (result.IsFailure)
            {
                await RestoreAsync(session, rewritten, ct).ConfigureAwait(false);
                await UndoMovesAsync(session, done, ct).ConfigureAwait(false);
                return result.Error;
            }
        }

        foreach (var (from, to) in done)
        {
            await events.PublishAsync(new EntryRenamed(session.Id, from, to), ct).ConfigureAwait(false);
        }

        foreach (var note in rewritten)
        {
            await events.PublishAsync(new NoteSaved(session.Id, note.Path), ct).ConfigureAwait(false);
        }

        return new MoveOutcome(done, [.. rewritten.Select(r => r.Path)]);
    }

    // Every indexed file affected by the moves (folders expand to their content).
    private static Dictionary<VaultPath, VaultPath> FileMap(MetadataSnapshot? snapshot, IReadOnlyList<(VaultPath From, VaultPath To)> moves)
    {
        var map = new Dictionary<VaultPath, VaultPath>();
        foreach (var (from, to) in moves)
        {
            map[from] = to;
            foreach (var path in snapshot?.Files.Keys.Where(p => p.IsDescendantOf(from)) ?? [])
            {
                map[path] = to.Combine(path.RelativeTo(from)).Value;
            }
        }

        return map;
    }

    private async Task<Result<Unit>> RewriteLinksAsync(VaultSession session, MetadataSnapshot before, Dictionary<VaultPath, VaultPath> map, List<NoteFile> rewritten, CancellationToken ct)
    {
        var names = map.Keys.Concat(map.Values).Select(p => p.Name.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var after = new PathListCatalog(before.Files.Keys.Select(p => map.GetValueOrDefault(p, p)));
        var candidates = before.Files.Values
            .Where(f => f.Kind == FileKind.Note && (map.ContainsKey(f.Path) || f.Metadata.AllLinks.Any(l => LinkNames.CandidateNames(l).Overlaps(names))))
            .Select(f => f.Path)
            .ToList();

        foreach (var sourceBefore in candidates)
        {
            var sourceAfter = map.GetValueOrDefault(sourceBefore, sourceBefore);
            var file = await NoteFiles.ReadAsync(session.FileSystem, sourceAfter, ct).ConfigureAwait(false);
            if (file.IsFailure)
            {
                continue; // removed meanwhile: nothing to rewrite
            }

            var text = file.Value.Content.Text;
            var links = analyzer.Analyze(text).AllLinks.ToList();
            var result = LinkRewriter.Rewrite(text, links, new RewriteRequest(sourceBefore, sourceAfter, map, before, after, session.Settings));
            if (result.ChangedLinks == 0)
            {
                continue;
            }

            var bytes = file.Value.Content.WithText(result.Text).ToBytes();
            var written = await session.FileSystem.WriteAllBytesAsync(sourceAfter, bytes, file.Value.Sha, ct).ConfigureAwait(false);
            if (written.IsFailure)
            {
                return written.Error;
            }

            rewritten.Add(file.Value);
        }

        return Result.Success();
    }

    private static async Task RestoreAsync(VaultSession session, List<NoteFile> rewritten, CancellationToken ct)
    {
        foreach (var original in rewritten)
        {
            await session.FileSystem.WriteAllBytesAsync(original.Path, original.Bytes, null, ct).ConfigureAwait(false);
        }
    }

    private static async Task UndoMovesAsync(VaultSession session, List<(VaultPath From, VaultPath To)> done, CancellationToken ct)
    {
        foreach (var (from, to) in Enumerable.Reverse(done))
        {
            await session.FileSystem.MoveAsync(to, from, ct).ConfigureAwait(false);
        }
    }
}
