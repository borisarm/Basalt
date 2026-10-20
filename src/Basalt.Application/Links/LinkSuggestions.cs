using System.Text.RegularExpressions;
using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Indexing;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.Markdown;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Links;
using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;
using Basalt.Domain.Search;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Links;

public enum LinkSuggestionKind
{
    Note,
    Alias,
    Attachment,
    Heading,
    Block,
}

/// <summary>
/// An autocompletion entry for <c>[[</c>. <see cref="Target"/> is the destination as it must be written (shortest
/// unambiguous form by default); headings and blocks add <see cref="Subpath"/>. A block without an id has
/// <see cref="Subpath"/> null and its <see cref="Line"/> set: accepting it calls <see cref="EnsureBlockId"/>.
/// </summary>
public sealed record LinkSuggestion(
    LinkSuggestionKind Kind,
    VaultPath Path,
    string Display,
    string Target,
    string? Subpath,
    string? Alias,
    IReadOnlyList<int> Positions,
    double Score,
    int? Line = null);

/// <summary>Autocompletion after <c>[[</c> (F-004.1–F-004.3): <c>pro</c>, <c>Nota#enc</c>, <c>Nota#^blo</c>, <c>#enc</c>.</summary>
public sealed record SuggestLinkTargets(VaultId VaultId, VaultPath SourcePath, string Partial, int Limit = 50) : IQuery<IReadOnlyList<LinkSuggestion>>, IVaultRequest;

public sealed class SuggestLinkTargetsHandler(IVaultSessions sessions, IMarkdownAnalyzer analyzer) : IQueryHandler<SuggestLinkTargets, IReadOnlyList<LinkSuggestion>>
{
    public async Task<Result<IReadOnlyList<LinkSuggestion>>> HandleAsync(SuggestLinkTargets query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var snapshot = session.Value.Get<IMetadataCache>().Current;
        var hash = query.Partial.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0)
        {
            return Result.Success<IReadOnlyList<LinkSuggestion>>([.. Files(query, snapshot, session.Value.Settings).Take(query.Limit)]);
        }

        var notePart = query.Partial[..hash];
        var target = notePart.Length == 0
            ? query.SourcePath
            : LinkResolver.Resolve(notePart, LinkKind.WikiLink, query.SourcePath, snapshot).TargetPath;
        if (target is not { } note)
        {
            return Result.Success<IReadOnlyList<LinkSuggestion>>([]);
        }

        var destination = LinkFormatter.FormatTarget(note, query.SourcePath, session.Value.Settings.LinkPathStyle, session.Value.Settings.LinkFormat, snapshot);
        var rest = query.Partial[(hash + 1)..];
        var suggestions = rest.StartsWith('^')
            ? await BlocksAsync(session.Value, note, destination, rest[1..], ct).ConfigureAwait(false)
            : Headings(snapshot, note, destination, rest);
        return Result.Success<IReadOnlyList<LinkSuggestion>>([.. suggestions.Take(query.Limit)]);
    }

    private static IEnumerable<LinkSuggestion> Files(SuggestLinkTargets query, MetadataSnapshot snapshot, VaultSettings settings)
    {
        var candidates = new List<(LinkSuggestionKind Kind, VaultPath Path, string Key)>();
        foreach (var file in snapshot.Files.Values)
        {
            if (file.Kind == FileKind.Note)
            {
                candidates.Add((LinkSuggestionKind.Note, file.Path, file.Path.NameWithoutExtension));
                candidates.AddRange(file.Metadata.Aliases.Select(a => (LinkSuggestionKind.Alias, file.Path, a)));
            }
            else
            {
                candidates.Add((LinkSuggestionKind.Attachment, file.Path, file.Path.Name));
            }
        }

        return FuzzyMatcher.Rank(query.Partial, candidates, c => c.Key).Select(r => new LinkSuggestion(
            r.Item.Kind,
            r.Item.Path,
            r.Item.Key,
            LinkFormatter.FormatTarget(r.Item.Path, query.SourcePath, settings.LinkPathStyle, settings.LinkFormat, snapshot),
            null,
            r.Item.Kind == LinkSuggestionKind.Alias ? r.Item.Key : null,
            r.Match.Positions,
            r.Match.Score));
    }

    private static IEnumerable<LinkSuggestion> Headings(MetadataSnapshot snapshot, VaultPath note, string destination, string partial)
    {
        var headings = snapshot.Files.GetValueOrDefault(note)?.Metadata.Headings ?? [];
        var matches = partial.Length == 0
            ? headings.Select(h => (Item: h, Match: new FuzzyMatch(0, [])))
            : FuzzyMatcher.Rank(partial, headings, h => h.Text);
        return matches.Select(m => new LinkSuggestion(LinkSuggestionKind.Heading, note, m.Item.Text, destination, m.Item.Text, null, m.Match.Positions, m.Match.Score, m.Item.Line));
    }

    private async Task<IEnumerable<LinkSuggestion>> BlocksAsync(VaultSession session, VaultPath note, string destination, string partial, CancellationToken ct)
    {
        var file = await NoteFiles.ReadAsync(session.FileSystem, note, ct).ConfigureAwait(false);
        if (file.IsFailure)
        {
            return [];
        }

        var text = file.Value.Content.Text;
        var needle = FoldedText.Fold(partial);
        return NoteBlocks.Of(text, analyzer.Analyze(text))
            .Where(b => needle.Length == 0 || FoldedText.Fold(b.Display).Contains(needle, StringComparison.Ordinal))
            .Select(b => new LinkSuggestion(LinkSuggestionKind.Block, note, b.Display, destination, b.Id is { } id ? "^" + id : null, null, [], 0, b.Block.LastLine));
    }
}

/// <summary>Returns the id of the block on <paramref name="Line"/>, adding <c>^xxxxxx</c> (six random characters) if it has none.</summary>
/// <returns><c>entry.not_found</c>, <c>block.not_found</c> when the line is not part of a block.</returns>
public sealed record EnsureBlockId(VaultId VaultId, VaultPath Path, int Line) : ICommand<BlockId>, IVaultRequest;

public sealed class EnsureBlockIdHandler(IVaultSessions sessions, IMarkdownAnalyzer analyzer, IRandom random, IEventBus events) : ICommandHandler<EnsureBlockId, BlockId>
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public async Task<Result<BlockId>> HandleAsync(EnsureBlockId command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        BlockId? result = null;
        var edited = await NoteEditing.EditAsync(sessions, events, command.VaultId, command.Path, text =>
        {
            var metadata = analyzer.Analyze(text);
            var block = NoteBlocks.Of(text, metadata).FirstOrDefault(b => b.Block.FirstLine <= command.Line && command.Line <= b.Block.LastLine);
            if (block is null)
            {
                return new Error(ErrorCodes.BlockNotFound, "There is no block on that line.");
            }

            if (block.Id is { } existing)
            {
                result = existing;
                return text;
            }

            var id = NewId(metadata.Blocks.Select(b => b.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase));
            result = id;
            var marker = block.Block.Kind == TextBlockKind.Code ? "\n^" + id : " ^" + id;
            return text.Insert(block.Block.Range.End, marker);
        }, ct).ConfigureAwait(false);

        return edited.IsFailure ? edited.Error : result!.Value;
    }

    private BlockId NewId(HashSet<string> taken)
    {
        while (true)
        {
            var id = string.Concat(Enumerable.Range(0, 6).Select(_ => Alphabet[random.Next(Alphabet.Length)]));
            if (!taken.Contains(id))
            {
                return BlockId.Create(id).Value;
            }
        }
    }
}

/// <summary>The blocks a <c>[[note#^</c> link can point at, with their ids (spec 05 §3.3).</summary>
internal static partial class NoteBlocks
{
    public sealed record Candidate(TextBlock Block, string Display, BlockId? Id);

    /// <summary>Paragraphs, list items and code blocks outside the frontmatter. A fenced block's id goes on the next line.</summary>
    public static IReadOnlyList<Candidate> Of(string text, NoteMetadata metadata)
    {
        var frontmatter = metadata.Frontmatter is { } fm ? new TextRange(fm.Span.Start, fm.Span.Length) : (TextRange?)null;
        var ids = metadata.Blocks.ToDictionary(b => b.Line, b => b.Id);
        var candidates = new List<Candidate>();
        foreach (var block in TextBlocks.Find(text))
        {
            var content = text.Substring(block.Range.Start, block.Range.Length);
            if (block.Kind == TextBlockKind.Heading || (frontmatter is { } f && f.Overlaps(block.Range)))
            {
                continue;
            }

            if (OnlyId().IsMatch(content) && candidates.Count > 0 && candidates[^1].Block.Kind == TextBlockKind.Code && candidates[^1].Block.LastLine == block.FirstLine - 1)
            {
                candidates[^1] = candidates[^1] with { Id = ids.TryGetValue(block.FirstLine, out var codeId) ? codeId : candidates[^1].Id };
                continue;
            }

            var display = TrailingId().Replace(content, string.Empty);
            candidates.Add(new Candidate(block, display, ids.TryGetValue(block.LastLine, out var id) ? id : null));
        }

        return candidates;
    }

    [GeneratedRegex(@"^\s*\^[A-Za-z0-9-]+\s*$")]
    private static partial Regex OnlyId();

    [GeneratedRegex(@"\s+\^[A-Za-z0-9-]+\s*$")]
    private static partial Regex TrailingId();
}
