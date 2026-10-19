using Basalt.Adapters.Markdown.Syntax;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Parsers.Inlines;

namespace Basalt.Adapters.Markdown;

/// <summary>The Markdig pipeline for Basalt's dialect (spec 05 §1): CommonMark + selected GFM + Obsidian syntax.</summary>
internal static class MarkdownPipelineFactory
{
    public static MarkdownPipeline Pipeline { get; } = Create();

    private static MarkdownPipeline Create()
    {
        var builder = new MarkdownPipelineBuilder()
            .UseYamlFrontMatter()
            .UsePipeTables()
            .UseTaskLists()
            .UseFootnotes()
            .UseAutoLinks()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough | EmphasisExtraOptions.Marked)
            .UseMathematics()
            .UsePreciseSourceLocation();

        // Wikilinks must win over Markdig's "[...]" link parser; comments and tags have their own opening characters.
        builder.InlineParsers.InsertBefore<LinkInlineParser>(new WikiLinkInlineParser());
        builder.InlineParsers.Insert(0, new CommentInlineParser());
        builder.InlineParsers.Add(new TagInlineParser());
        builder.BlockParsers.Insert(0, new CommentBlockParser());
        return builder.Build();
    }
}
