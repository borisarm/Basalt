using Basalt.Domain.Common;
using Basalt.Domain.Links;
using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;
using Basalt.Domain.Tags;

namespace Basalt.Domain.Tests.Metadata;

public sealed class NoteMetadataTests
{
    private static Link L(int start, LinkKind kind = LinkKind.WikiLink) =>
        new(kind, "t", null, SubpathKind.None, null, new SourceSpan(start, 3, 0, start), new SourceSpan(start + 2, 1, 0, start + 2));

    [Fact]
    public void AllLinks_MergesLinksAndEmbedsInDocumentOrder()
    {
        var metadata = NoteMetadata.Empty with { Links = [L(10), L(0)], Embeds = [L(5, LinkKind.Embed)] };

        metadata.AllLinks.Select(l => l.Span.Start).Should().Equal(0, 5, 10);
    }

    [Fact]
    public void DistinctTags_IgnoresCase()
    {
        var a = Tag.Create("Proyecto").Value;
        var b = Tag.Create("proyecto").Value;
        var metadata = NoteMetadata.Empty with { Tags = [new TagOccurrence(a, TagSource.Text, 0), new TagOccurrence(b, TagSource.Frontmatter, 1)] };

        metadata.DistinctTags.Should().ContainSingle().Which.Name.Should().Be("Proyecto");
    }

    [Fact]
    public void TaskItem_AndFrontmatter_Flags()
    {
        new TaskItem(0, ' ', "x").IsDone.Should().BeFalse();
        new TaskItem(0, 'x', "x").IsDone.Should().BeTrue();
        new TaskItem(0, '-', "x").IsDone.Should().BeTrue();
        new Frontmatter(StructuredValue.MapValue.Of(), new SourceSpan(0, 0, 0, 0)).IsValid.Should().BeTrue();
        new Frontmatter(StructuredValue.MapValue.Of(), new SourceSpan(0, 0, 0, 0), "bad").IsValid.Should().BeFalse();
        new BlockReference(BlockId.Create("a").Value, 3).Line.Should().Be(3);
    }
}
