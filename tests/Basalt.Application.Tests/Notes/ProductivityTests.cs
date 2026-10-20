using Basalt.Application.Bookmarks;
using Basalt.Application.Errors;
using Basalt.Application.Notes;
using Basalt.Application.Search;
using Basalt.Application.Templates;
using Basalt.Application.Tests.TestSupport;
using Basalt.Application.Workspace;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;
using static Basalt.Application.Tests.TestSupport.VaultFixture;

namespace Basalt.Application.Tests.Notes;

public sealed class ProductivityTests
{
    private static Dictionary<string, string> Files(params (string Path, string Text)[] files) => files.ToDictionary(f => f.Path, f => f.Text);

    private static readonly VaultSettings DailySettings = VaultSettings.Default with
    {
        DailyNotes = new DailyNotesSettings("Diario", "YYYY-MM-DD", "Plantillas/Diario.md"),
        TemplatesFolder = "Plantillas",
    };

    private static OpenDailyNoteHandler Daily(VaultFixture v) => new(v.Sessions, v.Events, v.Clock, v.Culture);

    [Fact]
    [Trait("Spec", "F-009.1")]
    public async Task DailyNote_IsCreatedFromTheTemplate_WithTheLocalDate()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("Plantillas/Diario.md", "# {{date:dddd D MMMM YYYY}}")), DailySettings);

        var result = (await Daily(v).HandleAsync(new OpenDailyNote(Id), Ct)).Value;

        result.Should().BeEquivalentTo(new { Path = P("Diario/2026-10-19.md"), Created = true }, o => o.ComparingByValue<VaultPath>());
        (await v.ReadAsync("Diario/2026-10-19.md")).Should().Be("# lunes 19 octubre 2026");
    }

    [Fact]
    [Trait("Spec", "F-009.2")]
    public async Task DailyNote_ThatExists_IsOpenedUnchanged()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("Plantillas/Diario.md", "plantilla"), ("Diario/2026-10-19.md", "mis notas")), DailySettings);

        var result = (await Daily(v).HandleAsync(new OpenDailyNote(Id), Ct)).Value;

        result.Created.Should().BeFalse();
        (await v.ReadAsync("Diario/2026-10-19.md")).Should().Be("mis notas");
    }

    [Fact]
    public async Task DailyNote_ForAnotherDate_WithoutTemplate_AndWithAMissingTemplate()
    {
        await using var v = await VaultFixture.CreateAsync(Files(), VaultSettings.Default with { DailyNotes = new DailyNotesSettings(DateFormat: "YYYY/MM/DD") });

        (await Daily(v).HandleAsync(new OpenDailyNote(Id, new DateOnly(2026, 1, 2)), Ct)).Value.Path.Should().Be(P("2026/01/02.md"));
        (await v.ReadAsync("2026/01/02.md")).Should().BeEmpty();

        v.Session.UpdateSettings(DailySettings);
        (await Daily(v).HandleAsync(new OpenDailyNote(Id), Ct)).Error.Code.Should().Be(ErrorCodes.EntryNotFound);
        (await v.ExistsAsync("Diario/2026-10-19.md")).Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "F-009.3")]
    public async Task InsertTemplate_RendersAtTheCursor_AndMergesFrontmatter_NoteWins()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("Plantillas/Reunión.md", "---\nstatus: plantilla\ntags: [reunion]\n---\n## {{title}}\n")), DailySettings);
        const string Current = "---\nstatus: draft\n---\nIntro\n";

        var result = (await new InsertTemplateHandler(v.Sessions, v.Analyzer, v.FrontmatterEditor, v.Clock, v.Culture)
            .HandleAsync(new InsertTemplate(Id, P("Plantillas/Reunión.md"), P("Reunión.md"), Current, Current.Length), Ct)).Value;

        result.Text.Should().Be("---\nstatus: draft\ntags:\n  - reunion\n---\nIntro\n## Reunión\n");
        result.Cursor.Should().Be(result.Text.Length);
    }

    [Fact]
    public async Task InsertTemplate_WithoutFrontmatter_JustInserts_AndClampsTheCursor()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("Plantillas/T.md", "[{{title}}]")), DailySettings);
        var handler = new InsertTemplateHandler(v.Sessions, v.Analyzer, v.FrontmatterEditor, v.Clock, v.Culture);

        var middle = (await handler.HandleAsync(new InsertTemplate(Id, P("Plantillas/T.md"), P("N.md"), "ab", 1), Ct)).Value;
        var beyond = (await handler.HandleAsync(new InsertTemplate(Id, P("Plantillas/T.md"), P("N.md"), "ab", 99), Ct)).Value;

        middle.Should().Be(new TemplateInsertion("a[N]b", 4));
        beyond.Should().Be(new TemplateInsertion("ab[N]", 5));
        (await handler.HandleAsync(new InsertTemplate(Id, P("x.md"), P("N.md"), "", 0), Ct)).Error.Code.Should().Be(ErrorCodes.EntryNotFound);
    }

    [Fact]
    public async Task InsertTemplate_AddsFrontmatterToANoteWithoutIt()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("Plantillas/T.md", "---\ntipo: idea\n---\nTexto")), DailySettings);

        var result = (await new InsertTemplateHandler(v.Sessions, v.Analyzer, v.FrontmatterEditor, v.Clock, v.Culture)
            .HandleAsync(new InsertTemplate(Id, P("Plantillas/T.md"), P("N.md"), "Hola ", 5), Ct)).Value;

        result.Text.Should().Be("---\ntipo: idea\n---\nHola Texto");
        result.Text[..result.Cursor].Should().EndWith("Hola Texto");
    }

    [Fact]
    public async Task ListTemplates_ListsNotesInTheTemplatesFolder()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("Plantillas/B.md", ""), ("Plantillas/sub/A.md", ""), ("Plantillas/img.png", ""), ("Otra.md", "")), DailySettings);

        (await new ListTemplatesHandler(v.Sessions).HandleAsync(new ListTemplates(Id), Ct)).Value.Should().Equal(P("Plantillas/B.md"), P("Plantillas/sub/A.md"));

        v.Session.UpdateSettings(VaultSettings.Default);
        (await new ListTemplatesHandler(v.Sessions).HandleAsync(new ListTemplates(Id), Ct)).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Bookmarks_AddListRemove_InGroups()
    {
        await using var v = await WithProductivityAsync(Files(("A.md", ""), ("B.md", "")));
        var add = new AddBookmarkHandler(v.Sessions);

        await add.HandleAsync(new AddBookmark(Id, Bookmark.ForFile("A.md")), Ct);
        await add.HandleAsync(new AddBookmark(Id, Bookmark.ForSearch("tag:#x"), "Trabajo"), Ct);
        await add.HandleAsync(new AddBookmark(Id, Bookmark.ForFile("B.md", "#Tema"), "Trabajo"), Ct);
        await add.HandleAsync(new AddBookmark(Id, Bookmark.ForFile("A.md")), Ct);

        var listed = (await new ListBookmarksHandler(v.Sessions).HandleAsync(new ListBookmarks(Id), Ct)).Value;
        listed.Document.Items.Should().HaveCount(2);
        listed.Document.Items[1].Items.Should().HaveCount(2);

        (await new RemoveBookmarkHandler(v.Sessions).HandleAsync(new RemoveBookmark(Id, Bookmark.ForSearch("tag:#x")), Ct)).IsSuccess.Should().BeTrue();
        (await new RemoveBookmarkHandler(v.Sessions).HandleAsync(new RemoveBookmark(Id, Bookmark.ForFolder("nada")), Ct)).Error.Code.Should().Be(ErrorCodes.BookmarkNotFound);
        (await new ListBookmarksHandler(v.Sessions).HandleAsync(new ListBookmarks(Id), Ct)).Value.Document.Items[1].Items.Should().ContainSingle();
    }

    [Fact]
    [Trait("Spec", "F-011.1")]
    public async Task Bookmark_SurvivesARename()
    {
        await using var v = await WithProductivityAsync(Files(("A.md", ""), ("Carpeta/X.md", "")));
        await new AddBookmarkHandler(v.Sessions).HandleAsync(new AddBookmark(Id, Bookmark.ForFile("A.md", "#Tema")), Ct);
        await new AddBookmarkHandler(v.Sessions).HandleAsync(new AddBookmark(Id, Bookmark.ForGroup("G", Bookmark.ForFile("Carpeta/X.md"), Bookmark.ForFolder("Carpeta"))), Ct);
        var mover = new EntryMover(v.Analyzer, v.Events);

        await new RenameEntryHandler(v.Sessions, mover).HandleAsync(new RenameEntry(Id, P("A.md"), "B"), Ct);
        await new RenameEntryHandler(v.Sessions, mover).HandleAsync(new RenameEntry(Id, P("Carpeta"), "Otra"), Ct);
        await v.Events.WaitForIdleAsync(Ct);

        var items = (await new ListBookmarksHandler(v.Sessions).HandleAsync(new ListBookmarks(Id), Ct)).Value.Document.Items;
        items[0].Should().Be(Bookmark.ForFile("B.md", "#Tema"));
        items[1].Items!.Select(i => i.Path).Should().Equal("Otra/X.md", "Otra");
    }

    [Fact]
    [Trait("Spec", "PERS-1.1")]
    public async Task InvalidBookmarks_AreReported()
    {
        await using var v = await WithProductivityAsync(Files());
        ((InMemoryVaultConfigStore)v.Session.Get<BookmarkList>().Store).CorruptBookmarks(v.FileSystem);

        var listed = (await new ListBookmarksHandler(v.Sessions).HandleAsync(new ListBookmarks(Id), Ct)).Value;

        listed.IsInvalid.Should().BeTrue();
        listed.Problem.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RecentFiles_AreMostRecentFirst_Bounded_AndFollowRenamesAndDeletions()
    {
        await using var v = await WithProductivityAsync(Files(("A.md", ""), ("B.md", ""), ("C.md", "")));
        var record = new RecordRecentFileHandler(v.Sessions);

        foreach (var name in new[] { "A.md", "B.md", "C.md", "A.md" })
        {
            await record.HandleAsync(new RecordRecentFile(Id, P(name)), Ct);
        }

        (await new ListRecentFilesHandler(v.Sessions).HandleAsync(new ListRecentFiles(Id), Ct)).Value.Should().Equal(P("A.md"), P("C.md"), P("B.md"));

        await new RenameEntryHandler(v.Sessions, new EntryMover(v.Analyzer, v.Events)).HandleAsync(new RenameEntry(Id, P("C.md"), "D"), Ct);
        await new DeleteEntriesHandler(v.Sessions, v.Events).HandleAsync(new DeleteEntries(Id, [P("B.md")]), Ct);
        await v.Events.WaitForIdleAsync(Ct);
        (await new ListRecentFilesHandler(v.Sessions).HandleAsync(new ListRecentFiles(Id), Ct)).Value.Should().Equal(P("A.md"), P("D.md"));

        for (var i = 0; i < 60; i++)
        {
            await record.HandleAsync(new RecordRecentFile(Id, P($"n{i}.md")), Ct);
        }

        (await new ListRecentFilesHandler(v.Sessions).HandleAsync(new ListRecentFiles(Id), Ct)).Value.Should().HaveCount(RecentFiles.Capacity).And.StartWith(P("n59.md"));
    }

    [Fact]
    public async Task QuickSwitch_WithEmptyText_ShowsRecentFilesFirst()
    {
        await using var v = await WithProductivityAsync(Files(("A.md", ""), ("B.md", "")));
        await new RecordRecentFileHandler(v.Sessions).HandleAsync(new RecordRecentFile(Id, P("B.md")), Ct);

        (await new QuickSwitchHandler(v.Sessions).HandleAsync(new QuickSwitch(Id, ""), Ct)).Value.Select(i => i.Path).Should().Equal(P("B.md"));
    }

    [Fact]
    public async Task Outline_OfANote()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("n.md", "# A\n## A1\n# B")));

        var outline = (await new GetOutlineHandler(v.Sessions, v.Analyzer).HandleAsync(new GetOutline(Id, P("n.md")), Ct)).Value;

        outline.Select(o => (o.Heading.Text, o.Children.Count)).Should().Equal(("A", 1), ("B", 0));
        (await new GetOutlineHandler(v.Sessions, v.Analyzer).HandleAsync(new GetOutline(Id, P("x.md")), Ct)).Error.Code.Should().Be(ErrorCodes.EntryNotFound);
    }

    [Fact]
    [Trait("Spec", "F-013")]
    public async Task Workspace_IsSavedPerDevice()
    {
        await using var v = await WithProductivityAsync(Files());
        var state = new WorkspaceState([new WorkspacePane([new WorkspaceTab("note", "A.md")])]);
        var device = new FakeDeviceInfo();

        (await new LoadWorkspaceHandler(v.Sessions, v.Session.Get<BookmarkList>().Store, device).HandleAsync(new LoadWorkspace(Id), Ct)).Value.Should().BeNull();
        await new SaveWorkspaceHandler(v.Sessions, v.Session.Get<BookmarkList>().Store, device).HandleAsync(new SaveWorkspace(Id, state), Ct);

        (await new LoadWorkspaceHandler(v.Sessions, v.Session.Get<BookmarkList>().Store, device).HandleAsync(new LoadWorkspace(Id), Ct)).Value.Should().BeEquivalentTo(state);
    }

    private static async Task<VaultFixture> WithProductivityAsync(Dictionary<string, string> files)
    {
        var v = await VaultFixture.CreateAsync(files);
        await new ProductivitySessionInitializer(new InMemoryVaultConfigStore(), new InMemoryRecentFilesStore(), v.Events).InitializeAsync(v.Session, Ct);
        return v;
    }
}
