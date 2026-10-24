using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Indexing;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.Index;
using Basalt.Application.Ports.Markdown;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Links;
using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;
using Basalt.Domain.Search;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Links;

/// <summary>The line around a link or mention, with the highlighted part relative to <see cref="Text"/>.</summary>
public sealed record LinkContext(int Line, string Text, TextRange Highlight);

public sealed record LinkedMention(Link Link, LinkContext Context);

public sealed record BacklinkGroup(VaultPath Source, IReadOnlyList<LinkedMention> Links);

public sealed record UnlinkedMention(TextRange Range, string Name, LinkContext Context);

public sealed record UnlinkedMentionGroup(VaultPath Source, IReadOnlyList<UnlinkedMention> Mentions);

public sealed record Backlinks(IReadOnlyList<BacklinkGroup> Linked, IReadOnlyList<UnlinkedMentionGroup> Unlinked);

/// <summary>
/// Linked and unlinked mentions of a file (F-005). Sources are re-read and re-analysed so that spans and contexts match
/// the files as they are now; unlinked mentions are only computed for notes (title and aliases).
/// </summary>
public sealed record GetBacklinks(VaultId VaultId, VaultPath Path, bool IncludeUnlinked = true) : IQuery<Backlinks>, IVaultRequest;

public sealed class GetBacklinksHandler(IVaultSessions sessions, IMarkdownAnalyzer analyzer) : IQueryHandler<GetBacklinks, Backlinks>
{
    public async Task<Result<Backlinks>> HandleAsync(GetBacklinks query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var snapshot = session.Value.Get<IMetadataCache>().Current;
        var linked = new List<BacklinkGroup>();
        foreach (var source in snapshot.Graph.Incoming(query.Path).Select(l => l.Source).Distinct().OrderBy(p => p.ToString(), StringComparer.Ordinal))
        {
            var file = await NoteFiles.ReadAsync(session.Value.FileSystem, source, ct).ConfigureAwait(false);
            if (file.IsFailure)
            {
                continue;
            }

            var text = file.Value.Content.Text;
            var cached = snapshot.Files.GetValueOrDefault(source);
            var metadata = cached is not null && cached.Stamp.Sha == file.Value.Sha
                ? cached.Metadata
                : analyzer.Analyze(text);
            var links = metadata.AllLinks
                .Where(l => LinkResolver.Resolve(l, source, snapshot).TargetPath == query.Path)
                .Select(l => new LinkedMention(l, LinkTexts.Context(text, new TextRange(l.Span.Start, l.Span.Length))))
                .ToList();
            if (links.Count > 0)
            {
                linked.Add(new BacklinkGroup(source, links));
            }
        }

        var unlinked = query.IncludeUnlinked && FileClassifier.Classify(query.Path) == FileKind.Note
            ? await UnlinkedAsync(session.Value, snapshot, query.Path, ct).ConfigureAwait(false)
            : [];
        return new Backlinks(linked, unlinked);
    }

    private async Task<List<UnlinkedMentionGroup>> UnlinkedAsync(VaultSession session, MetadataSnapshot snapshot, VaultPath target, CancellationToken ct)
    {
        var names = LinkTexts.NamesOf(target, snapshot);

        // The index narrows the candidates to notes containing a name; the exact rules are applied on the text.
        var phrases = names.Select(n => (SearchNode)new FieldNode(SearchField.Content, new PhraseNode(n))).ToList();
        var query = new SearchQuery(phrases.Count == 1 ? phrases[0] : new OrNode([.. phrases]));
        var hits = await session.Get<INoteIndexReader>().SearchAsync(query, SearchOptions.Default, ct).ConfigureAwait(false);

        var groups = new List<UnlinkedMentionGroup>();
        foreach (var source in hits.Where(h => h.Kind == FileKind.Note && h.Path != target).Select(h => h.Path).OrderBy(p => p.ToString(), StringComparer.Ordinal))
        {
            var file = await NoteFiles.ReadAsync(session.FileSystem, source, ct).ConfigureAwait(false);
            if (file.IsFailure)
            {
                continue;
            }

            var text = file.Value.Content.Text;
            var mentions = MentionFinder.Find(text, names, LinkTexts.Excluded(text, analyzer.Analyze(text)))
                .Select(m => new UnlinkedMention(m.Range, m.Name, LinkTexts.Context(text, m.Range)))
                .ToList();
            if (mentions.Count > 0)
            {
                groups.Add(new UnlinkedMentionGroup(source, mentions));
            }
        }

        return groups;
    }
}

public sealed record OutgoingLink(Link Link, LinkResolution Resolution);

/// <summary>Links of a note to vault files, resolved or not (external URLs excluded), in document order.</summary>
/// <returns><c>entry.not_found</c> when the note does not exist.</returns>
public sealed record GetOutgoingLinks(VaultId VaultId, VaultPath Path) : IQuery<IReadOnlyList<OutgoingLink>>, IVaultRequest;

public sealed class GetOutgoingLinksHandler(IVaultSessions sessions, IMarkdownAnalyzer analyzer) : IQueryHandler<GetOutgoingLinks, IReadOnlyList<OutgoingLink>>
{
    public async Task<Result<IReadOnlyList<OutgoingLink>>> HandleAsync(GetOutgoingLinks query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var file = await NoteFiles.ReadAsync(session.Value.FileSystem, query.Path, ct).ConfigureAwait(false);
        if (file.IsFailure)
        {
            return file.Error;
        }

        var snapshot = session.Value.Get<IMetadataCache>().Current;
        return Result.Success<IReadOnlyList<OutgoingLink>>([.. analyzer.Analyze(file.Value.Content.Text).AllLinks
            .Where(l => !LinkResolver.IsExternal(l.RawTarget))
            .Select(l => new OutgoingLink(l, LinkResolver.Resolve(l, query.Path, snapshot)))]);
    }
}

/// <summary>Turns an unlinked mention into a link (F-005.2), with the mention text as alias when it differs from the title.</summary>
/// <returns><c>mention.not_found</c> when <paramref name="Range"/> is no longer a mention of the target.</returns>
public sealed record LinkUnlinkedMention(VaultId VaultId, VaultPath SourcePath, TextRange Range, VaultPath TargetPath) : ICommand<Unit>, IVaultRequest;

public sealed class LinkUnlinkedMentionHandler(IVaultSessions sessions, IMarkdownAnalyzer analyzer, IEventBus events) : ICommandHandler<LinkUnlinkedMention, Unit>
{
    public Task<Result<Unit>> HandleAsync(LinkUnlinkedMention command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = sessions.Get(command.VaultId);
        if (session.IsFailure)
        {
            return Task.FromResult(Result.Failure(session.Error));
        }

        return NoteEditing.EditAsync(sessions, events, command.VaultId, command.SourcePath, text =>
        {
            var snapshot = session.Value.Get<IMetadataCache>().Current;
            var mentions = MentionFinder.Find(text, LinkTexts.NamesOf(command.TargetPath, snapshot), LinkTexts.Excluded(text, analyzer.Analyze(text)));
            if (!mentions.Any(m => m.Range == command.Range))
            {
                return new Error(ErrorCodes.MentionNotFound, "The text is no longer a mention of the note.");
            }

            var spelled = text.Substring(command.Range.Start, command.Range.Length);
            var alias = spelled == command.TargetPath.NameWithoutExtension ? null : spelled;
            var link = LinkFormatter.FormatLink(command.TargetPath, command.SourcePath, session.Value.Settings, snapshot, alias: alias);
            return string.Concat(text.AsSpan(0, command.Range.Start), link, text.AsSpan(command.Range.End));
        }, ct);
    }
}

/// <summary>Shared helpers for link panels.</summary>
internal static class LinkTexts
{
    /// <summary>The names a note is mentioned by: its title and its aliases.</summary>
    public static IReadOnlyList<string> NamesOf(VaultPath note, MetadataSnapshot snapshot) =>
        [note.NameWithoutExtension, .. snapshot.Files.GetValueOrDefault(note)?.Metadata.Aliases ?? []];

    /// <summary>Where mentions do not count: links, embeds, frontmatter and code.</summary>
    public static IEnumerable<TextRange> Excluded(string text, NoteMetadata metadata)
    {
        foreach (var link in metadata.AllLinks)
        {
            yield return new TextRange(link.Span.Start, link.Span.Length);
        }

        if (metadata.Frontmatter is { } frontmatter)
        {
            yield return new TextRange(frontmatter.Span.Start, frontmatter.Span.Length);
        }

        foreach (var code in CodeRegions.Find(text))
        {
            yield return code;
        }
    }

    /// <summary>The line containing the start of <paramref name="range"/>, with the range clipped to it.</summary>
    public static LinkContext Context(string text, TextRange range)
    {
        var lineStart = range.Start == 0 ? 0 : text.LastIndexOf('\n', range.Start - 1) + 1;
        var newline = text.IndexOf('\n', range.Start);
        var lineEnd = newline < 0 ? text.Length : newline;
        if (lineEnd > lineStart && text[lineEnd - 1] == '\r')
        {
            lineEnd--;
        }

        var line = text.AsSpan(0, lineStart).Count('\n');
        var highlight = new TextRange(range.Start - lineStart, Math.Min(range.End, lineEnd) - range.Start);
        return new LinkContext(line, text[lineStart..lineEnd], highlight);
    }
}
