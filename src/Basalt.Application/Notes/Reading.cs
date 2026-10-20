using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Indexing;
using Basalt.Application.Ports.Markdown;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Links;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Notes;

/// <summary>The reading-mode model of a text (the editor's current text, not necessarily saved).</summary>
public sealed record RenderNote(VaultId VaultId, VaultPath Path, string Text) : IQuery<RenderDocument>, IVaultRequest;

public sealed class RenderNoteHandler(IVaultSessions sessions, IMarkdownRenderer renderer) : IQueryHandler<RenderNote, RenderDocument>
{
    public Task<Result<RenderDocument>> HandleAsync(RenderNote query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        return Task.FromResult(session.IsFailure
            ? Result.Failure<RenderDocument>(session.Error)
            : Result.Success(renderer.Render(query.Text, new RenderContext(query.Path))));
    }
}

/// <summary>Where a link of a note points (null when unresolved).</summary>
public sealed record ResolveLink(VaultId VaultId, VaultPath SourcePath, string Target) : IQuery<VaultPath?>, IVaultRequest;

public sealed class ResolveLinkHandler(IVaultSessions sessions) : IQueryHandler<ResolveLink, VaultPath?>
{
    public Task<Result<VaultPath?>> HandleAsync(ResolveLink query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return Task.FromResult(Result.Failure<VaultPath?>(session.Error));
        }

        var snapshot = session.Value.Get<IMetadataCache>().Current;
        return Task.FromResult(Result.Success(LinkResolver.Resolve(query.Target, LinkKind.WikiLink, query.SourcePath, snapshot).TargetPath));
    }
}

/// <summary>What an embed shows in reading mode (spec 07 §6).</summary>
public abstract record EmbedContent;

/// <summary>A note, a heading's section or a single block.</summary>
public sealed record NoteEmbed(VaultPath Path, RenderDocument Document) : EmbedContent;

public sealed record ImageEmbed(VaultPath Path) : EmbedContent;

/// <summary>PDF, audio, video or another file: shown as a link.</summary>
public sealed record FileEmbed(VaultPath Path, FileKind Kind) : EmbedContent;

/// <summary>The target (or its heading/block) does not exist.</summary>
public sealed record MissingEmbed(string Target) : EmbedContent;

/// <summary>Not expanded: the note is already being shown higher up, or the nesting is deeper than <see cref="GetEmbedHandler.MaxDepth"/>.</summary>
public sealed record CycleEmbed(VaultPath Path) : EmbedContent;

/// <summary>
/// Resolves an embed (<c>![[target#subpath]]</c>) of <paramref name="SourcePath"/>. <paramref name="Ancestors"/> are the
/// notes already being displayed around it, outermost first (cycle detection and depth limit).
/// </summary>
public sealed record GetEmbed(VaultId VaultId, VaultPath SourcePath, string Target, string? Subpath, IReadOnlyList<VaultPath> Ancestors) : IQuery<EmbedContent>, IVaultRequest;

public sealed class GetEmbedHandler(IVaultSessions sessions, IMarkdownRenderer renderer) : IQueryHandler<GetEmbed, EmbedContent>
{
    /// <summary>Spec 07 §6: embeds are expanded at most three levels deep.</summary>
    public const int MaxDepth = 3;

    public async Task<Result<EmbedContent>> HandleAsync(GetEmbed query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var display = query.Subpath is null ? query.Target : $"{query.Target}#{query.Subpath}";
        var snapshot = session.Value.Get<IMetadataCache>().Current;
        if (LinkResolver.Resolve(query.Target, LinkKind.Embed, query.SourcePath, snapshot).TargetPath is not { } path)
        {
            return new MissingEmbed(display);
        }

        var kind = FileClassifier.Classify(path);
        if (kind != FileKind.Note)
        {
            return kind == FileKind.Image ? new ImageEmbed(path) : new FileEmbed(path, kind);
        }

        if (query.Ancestors.Contains(path) || query.Ancestors.Count >= MaxDepth)
        {
            return new CycleEmbed(path);
        }

        var file = await NoteFiles.ReadAsync(session.Value.FileSystem, path, ct).ConfigureAwait(false);
        if (file.IsFailure)
        {
            return new MissingEmbed(display);
        }

        var document = renderer.Render(file.Value.Content.Text, new RenderContext(path));
        var blocks = Part(document.Blocks, query.Subpath);
        return blocks is null ? new MissingEmbed(display) : new NoteEmbed(path, new RenderDocument(blocks));
    }

    private static IReadOnlyList<RenderBlock>? Part(IReadOnlyList<RenderBlock> blocks, string? subpath) => subpath switch
    {
        null => blocks,
        ['^', .. var id] => FindBlock(blocks, id) is { } block ? [block] : null,
        var heading => Section(blocks, heading),
    };

    private static RenderBlock? FindBlock(IEnumerable<RenderBlock> blocks, string id)
    {
        foreach (var block in blocks)
        {
            if (string.Equals(block.BlockId, id, StringComparison.OrdinalIgnoreCase))
            {
                return block;
            }

            var nested = block switch
            {
                ListBlock list => FindBlock(list.Items, id),
                ListItemBlock item => FindBlock(item.Blocks, id),
                QuoteBlock quote => FindBlock(quote.Blocks, id),
                CalloutBlock callout => FindBlock(callout.Blocks, id),
                _ => null,
            };
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>The heading and everything up to the next heading of the same or a higher level.</summary>
    private static List<RenderBlock>? Section(IReadOnlyList<RenderBlock> blocks, string anchor)
    {
        var start = -1;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is HeadingBlock heading && Heading.Create(heading.Level, PlainText(heading.Inlines), heading.Line) is { IsSuccess: true } parsed && parsed.Value.MatchesAnchor(anchor))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        var level = ((HeadingBlock)blocks[start]).Level;
        var end = start + 1;
        while (end < blocks.Count && !(blocks[end] is HeadingBlock next && next.Level <= level))
        {
            end++;
        }

        return [.. blocks.Skip(start).Take(end - start)];
    }

    private static string PlainText(IEnumerable<RenderInline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        TextInline text => text.Text,
        CodeInline code => code.Code,
        EmphasisInline e => PlainText(e.Children),
        StrongInline s => PlainText(s.Children),
        StrikethroughInline s => PlainText(s.Children),
        HighlightInline h => PlainText(h.Children),
        LinkInline l => PlainText(l.Children),
        TagInline t => "#" + t.Tag,
        _ => string.Empty,
    }));
}

/// <summary>The bytes of an attachment (images in reading mode).</summary>
/// <returns><c>entry.not_found</c>.</returns>
public sealed record ReadAttachment(VaultId VaultId, VaultPath Path) : IQuery<byte[]>, IVaultRequest;

public sealed class ReadAttachmentHandler(IVaultSessions sessions) : IQueryHandler<ReadAttachment, byte[]>
{
    public async Task<Result<byte[]>> HandleAsync(ReadAttachment query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var bytes = await session.Value.FileSystem.ReadAllBytesAsync(query.Path, ct).ConfigureAwait(false);
        return bytes.IsFailure && bytes.Error.Code == ErrorCodes.FsNotFound ? NoteFiles.NotFound(query.Path) : bytes;
    }
}
