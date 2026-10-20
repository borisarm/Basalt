using Basalt.Application.Ports.Index;
using Basalt.Domain.Common;
using Basalt.Domain.Links;
using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Tags;

namespace Basalt.Testing.Contracts;

/// <summary>Behaviour of <see cref="INoteIndex"/> (spec 03 §1.3, 08 §2) for the fake and the SQLite adapter.</summary>
public abstract class NoteIndexContract : ContractTest<INoteIndex>
{
    protected static VaultPath P(string path) => VaultPath.Create(path).Value;

    protected static IndexedNote Note(string path, string text, NoteMetadata? metadata = null, long size = 10)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return new IndexedNote(
            P(path),
            FileClassifier.Classify(P(path)),
            new IndexStamp(size, new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero), GitBlobHasher.Compute(bytes)),
            text,
            metadata ?? NoteMetadata.Empty);
    }

    protected static NoteMetadata RichMetadata()
    {
        var link = new Link(LinkKind.WikiLink, "B", "H", SubpathKind.Heading, "alias", new SourceSpan(10, 12, 2, 3), new SourceSpan(12, 1, 2, 5));
        var embed = new Link(LinkKind.Embed, "img.png", null, SubpathKind.None, "300", new SourceSpan(30, 18, 3, 0), new SourceSpan(33, 7, 3, 3));
        return new NoteMetadata(
            new Frontmatter(
                StructuredValue.MapValue.Of(
                    ("status", new StructuredValue.TextValue("draft")),
                    ("rating", new StructuredValue.NumberValue("4.5")),
                    ("due", new StructuredValue.DateValue("2026-10-19")),
                    ("done", new StructuredValue.BoolValue(false)),
                    ("tags", new StructuredValue.ListValue([new StructuredValue.TextValue("a")])),
                    ("meta", StructuredValue.MapValue.Of(("x", StructuredValue.NullValue.Instance)))),
                new SourceSpan(0, 40, 0, 0)),
            [Heading.Create(1, "Título", 5).Value, Heading.Create(2, "Sub", 8).Value],
            [link],
            [embed],
            [new TagOccurrence(Tag.Create("Proyecto/x").Value, TagSource.Text, 6), new TagOccurrence(Tag.Create("a").Value, TagSource.Frontmatter, 1)],
            [new BlockReference(BlockId.Create("b1").Value, 7)],
            [new TaskItem(9, ' ', "pending"), new TaskItem(10, 'x', "done")],
            ["Alias Uno"],
            42);
    }

    [Fact]
    public async Task Upsert_ThenGet_RoundTripsEverything()
    {
        var note = Note("Notas/A.md", "# Título\ncontenido", RichMetadata());

        await Sut.UpsertAsync(note, Ct);

        (await Sut.GetAsync(P("Notas/A.md"), Ct)).Should().BeEquivalentTo(note, o => o.ComparingByMembers<NoteMetadata>().ComparingByValue<VaultPath>().ComparingByValue<Tag>());
    }

    [Fact]
    public async Task Upsert_ReplacesExistingEntry()
    {
        await Sut.UpsertAsync(Note("a.md", "v1"), Ct);
        await Sut.UpsertAsync(Note("a.md", "v2", size: 20), Ct);

        var stored = await Sut.GetAsync(P("a.md"), Ct);
        stored!.Text.Should().Be("v2");
        stored.Stamp.Size.Should().Be(20);
        (await Sut.GetAllSummariesAsync(Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Attachments_HaveNoText()
    {
        var image = Note("img/foto.png", "ignored") with { Text = null };

        await Sut.UpsertAsync(image, Ct);

        var stored = await Sut.GetAsync(P("img/foto.png"), Ct);
        stored!.Kind.Should().Be(FileKind.Image);
        stored.Text.Should().BeNull();
    }

    [Fact]
    public async Task Get_Missing_IsNull()
    {
        (await Sut.GetAsync(P("missing.md"), Ct)).Should().BeNull();
    }

    [Fact]
    public async Task UpsertMany_AndSummaries()
    {
        await Sut.UpsertManyAsync([Note("a.md", "a", RichMetadata()), Note("b/c.md", "c"), Note("d.pdf", "x") with { Text = null }], Ct);

        var summaries = await Sut.GetAllSummariesAsync(Ct);

        summaries.Select(s => s.Path.ToString()).Order(StringComparer.Ordinal).Should().Equal("a.md", "b/c.md", "d.pdf");
        summaries.Single(s => s.Path == P("a.md")).Metadata.Should().BeEquivalentTo(RichMetadata(), o => o.ComparingByMembers<NoteMetadata>().ComparingByValue<VaultPath>().ComparingByValue<Tag>());
    }

    [Fact]
    public async Task Remove_DeletesTheEntry()
    {
        await Sut.UpsertAsync(Note("a.md", "a"), Ct);

        await Sut.RemoveAsync(P("a.md"), Ct);
        await Sut.RemoveAsync(P("never.md"), Ct);

        (await Sut.GetAsync(P("a.md"), Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Rename_MovesTheEntryAndKeepsContent()
    {
        await Sut.UpsertAsync(Note("old.md", "text", RichMetadata()), Ct);

        await Sut.RenameAsync(P("old.md"), P("Dir/new.md"), Ct);

        (await Sut.GetAsync(P("old.md"), Ct)).Should().BeNull();
        var moved = await Sut.GetAsync(P("Dir/new.md"), Ct);
        moved!.Text.Should().Be("text");
        moved.Metadata.Should().BeEquivalentTo(RichMetadata(), o => o.ComparingByMembers<NoteMetadata>().ComparingByValue<VaultPath>().ComparingByValue<Tag>());
    }

    [Fact]
    public async Task SnapshotInfo_ListsStamps_AndResetClears()
    {
        await Sut.ResetAsync(7, Ct);
        await Sut.UpsertAsync(Note("a.md", "a", size: 3), Ct);

        var info = await Sut.GetSnapshotInfoAsync(Ct);
        info.AnalyzerVersion.Should().Be(7);
        info.Entries[P("a.md")].Size.Should().Be(3);
        info.Entries[P("a.md")].Sha.Should().Be(GitBlobHasher.Compute("a"u8));

        await Sut.ResetAsync(8, Ct);

        var after = await Sut.GetSnapshotInfoAsync(Ct);
        after.AnalyzerVersion.Should().Be(8);
        after.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task UnicodePaths_AndText_RoundTrip()
    {
        await Sut.UpsertAsync(Note("Reunión 😀/Café.md", "日本語 ñ 😀"), Ct);

        (await Sut.GetAsync(P("Reunión 😀/Café.md"), Ct))!.Text.Should().Be("日本語 ñ 😀");
    }
}
