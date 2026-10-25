using System.Text;
using Basalt.Application.Errors;
using Basalt.Application.Notes;
using Basalt.Application.Vaults;
using Basalt.Application.Tests.TestSupport;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;
using static Basalt.Application.Tests.TestSupport.VaultFixture;

namespace Basalt.Application.Tests.Notes;

public sealed class FileUseCasesTests
{
    private static Dictionary<string, string> Files(params (string Path, string Text)[] files) => files.ToDictionary(f => f.Path, f => f.Text);

    [Fact]
    [Trait("Spec", "F-002.1")]
    public async Task CreateNote_InSelectedFolder_IsUntitled()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("Proyectos/x.md", "x")));
        var handler = new CreateNoteHandler(v.Sessions, v.Events, v.Clock, v.Culture);

        var created = await handler.HandleAsync(new CreateNote(Id, P("Proyectos")), Ct);

        created.Value.Should().Be(P("Proyectos/Untitled.md"));
        (await v.ReadAsync("Proyectos/Untitled.md")).Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "F-002.2")]
    public async Task CreateNote_PicksTheNextFreeUntitledName()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("Untitled.md", ""), ("Untitled 1.md", "")));
        var handler = new CreateNoteHandler(v.Sessions, v.Events, v.Clock, v.Culture);

        (await handler.HandleAsync(new CreateNote(Id), Ct)).Value.Should().Be(P("Untitled 2.md"));
    }

    [Fact]
    public async Task CreateNote_WithTitleTemplateAndDefaultFolder()
    {
        await using var v = await VaultFixture.CreateAsync(
            Files(("Plantillas/Reunión.md", "# {{title}}\n{{date:dddd D MMMM YYYY}}")),
            VaultSettings.Default with { NewNoteFolder = "Inbox" });
        var handler = new CreateNoteHandler(v.Sessions, v.Events, v.Clock, v.Culture);

        var created = await handler.HandleAsync(new CreateNote(Id, Title: "Reunión equipo", TemplatePath: P("Plantillas/Reunión.md")), Ct);

        created.Value.Should().Be(P("Inbox/Reunión equipo.md"));
        (await v.ReadAsync("Inbox/Reunión equipo.md")).Should().Be("# Reunión equipo\nlunes 19 octubre 2026");
        (await handler.HandleAsync(new CreateNote(Id, Title: "Reunión equipo"), Ct)).Error.Code.Should().Be(ErrorCodes.PathAlreadyExists);
        (await handler.HandleAsync(new CreateNote(Id, Title: "a:b"), Ct)).Error.Code.Should().Be(DomainErrors.PathInvalidSegment);
        (await handler.HandleAsync(new CreateNote(Id, TemplatePath: P("missing.md")), Ct)).Error.Code.Should().Be(ErrorCodes.EntryNotFound);
    }

    [Fact]
    public async Task CreateFolder_AndDuplicates()
    {
        await using var v = await VaultFixture.CreateAsync();
        var handler = new CreateFolderHandler(v.Sessions, v.Events);

        (await handler.HandleAsync(new CreateFolder(Id, P("Nueva")), Ct)).IsSuccess.Should().BeTrue();
        (await handler.HandleAsync(new CreateFolder(Id, P("Nueva")), Ct)).Error.Code.Should().Be(ErrorCodes.PathAlreadyExists);
    }

    [Fact]
    [Trait("Spec", "F-003.1")]
    public async Task SaveNote_KeepsCrLfAndBom_AndReturnsTheNewSha()
    {
        await using var v = await VaultFixture.CreateAsync();
        await v.FileSystem.WriteAllBytesAsync(P("n.md"), (byte[])[0xEF, 0xBB, 0xBF, .. "a\r\nb\r\n"u8], null, Ct);
        var document = (await new ReadNoteHandler(v.Sessions, v.Analyzer).HandleAsync(new ReadNote(Id, P("n.md")), Ct)).Value;

        var sha = await new SaveNoteHandler(v.Sessions, v.Events).HandleAsync(new SaveNote(Id, P("n.md"), "a\nb\nc\n", document.Sha), Ct);

        var bytes = (await v.FileSystem.ReadAllBytesAsync(P("n.md"), Ct)).Value;
        bytes.Should().Equal([0xEF, 0xBB, 0xBF, .. "a\r\nb\r\nc\r\n"u8]);
        sha.Value.Should().Be(GitBlobHasher.Compute(bytes));
    }

    [Fact]
    [Trait("Spec", "F-003.3")]
    public async Task SaveNote_WhenTheFileChangedExternally_ReturnsTheCurrentText()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("n.md", "original")));
        var loaded = (await new ReadNoteHandler(v.Sessions, v.Analyzer).HandleAsync(new ReadNote(Id, P("n.md")), Ct)).Value;
        v.FileSystem.SimulateExternalWrite(P("n.md"), "changed elsewhere");

        var result = await new SaveNoteHandler(v.Sessions, v.Events).HandleAsync(new SaveNote(Id, P("n.md"), "my edit", loaded.Sha), Ct);

        result.Error.Code.Should().Be(ErrorCodes.NoteModifiedExternally);
        result.Error.Metadata!["currentText"].Should().Be("changed elsewhere");
        (await v.ReadAsync("n.md")).Should().Be("changed elsewhere");
    }

    [Fact]
    public async Task SaveNote_NewFile_AndReadMissing()
    {
        await using var v = await VaultFixture.CreateAsync();

        (await new SaveNoteHandler(v.Sessions, v.Events).HandleAsync(new SaveNote(Id, P("new.md"), "hola", null), Ct)).IsSuccess.Should().BeTrue();

        (await v.ReadAsync("new.md")).Should().Be("hola");
        (await new ReadNoteHandler(v.Sessions, v.Analyzer).HandleAsync(new ReadNote(Id, P("x.md")), Ct)).Error.Code.Should().Be(ErrorCodes.EntryNotFound);
    }

    [Fact]
    [Trait("Spec", "F-002.3")]
    public async Task Rename_OntoAnExistingName_ChangesNothing()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "a"), ("B.md", "b")));

        var result = await new RenameEntryHandler(v.Sessions, new EntryMover(v.Analyzer, v.Events)).HandleAsync(new RenameEntry(Id, P("A.md"), "B"), Ct);

        result.Error.Code.Should().Be(ErrorCodes.PathAlreadyExists);
        (await v.ReadAsync("A.md")).Should().Be("a");
        (await v.ReadAsync("B.md")).Should().Be("b");
    }

    [Fact]
    [Trait("Spec", "F-002.4")]
    public async Task Rename_CaseOnly_Works()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("nota.md", "x")));

        var result = await new RenameEntryHandler(v.Sessions, new EntryMover(v.Analyzer, v.Events)).HandleAsync(new RenameEntry(Id, P("nota.md"), "Nota"), Ct);

        result.Value.Should().Be(P("Nota.md"));
        (await ListAsync(v)).Should().Equal("Nota.md");
    }

    [Fact]
    [Trait("Spec", "F-004.4")]
    public async Task Rename_UpdatesEveryLinkFormat_AndNothingElse()
    {
        const string Text = "Intro\n[[B]] y [[B#Sección|alias]] y [texto](B.md)\n`[[B]]`\nfin";
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", Text), ("B.md", "# Sección")));

        await new RenameEntryHandler(v.Sessions, new EntryMover(v.Analyzer, v.Events)).HandleAsync(new RenameEntry(Id, P("B.md"), "C"), Ct);

        (await v.ReadAsync("A.md")).Should().Be("Intro\n[[C]] y [[C#Sección|alias]] y [texto](C.md)\n`[[B]]`\nfin");
    }

    [Fact]
    [Trait("Spec", "F-004.4")]
    public async Task Rename_WhenTheSourceChangedAfterIndexing_UsesTheCurrentText()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "[[B]]"), ("B.md", "")));
        await v.FileSystem.WriteAllBytesAsync(P("A.md"), Encoding.UTF8.GetBytes("[[B]] y [[B]]"), null, Ct);

        await new RenameEntryHandler(v.Sessions, new EntryMover(v.Analyzer, v.Events)).HandleAsync(new RenameEntry(Id, P("B.md"), "C"), Ct);

        (await v.ReadAsync("A.md")).Should().Be("[[C]] y [[C]]");
    }

    [Fact]
    [Trait("Spec", "F-004.5")]
    public async Task RenameFolder_UpdatesExplicitPaths()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "[[X/Nota]] [[X/Sub/Otra|o]]"), ("X/Nota.md", "n"), ("X/Sub/Otra.md", "o")));

        await new RenameEntryHandler(v.Sessions, new EntryMover(v.Analyzer, v.Events)).HandleAsync(new RenameEntry(Id, P("X"), "Y"), Ct);

        (await v.ReadAsync("A.md")).Should().Be("[[Y/Nota]] [[Y/Sub/Otra|o]]");
        (await v.ExistsAsync("Y/Sub/Otra.md")).Should().BeTrue();
    }

    [Fact]
    [Trait("Spec", "F-004.6")]
    public async Task Rename_DoesNotTouchCodeBlocks()
    {
        const string Text = "```\n[[B]]\n```\n[[B]]";
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", Text), ("B.md", "")));

        await new RenameEntryHandler(v.Sessions, new EntryMover(v.Analyzer, v.Events)).HandleAsync(new RenameEntry(Id, P("B.md"), "C"), Ct);

        (await v.ReadAsync("A.md")).Should().Be("```\n[[B]]\n```\n[[C]]");
    }

    [Fact]
    [Trait("Spec", "F-004.7")]
    public async Task MovingAHomonym_LeavesAnExplicitLinkUnchanged()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "[[x/Nota]]"), ("x/Nota.md", ""), ("y/Nota.md", "")));

        await new MoveEntriesHandler(v.Sessions, new EntryMover(v.Analyzer, v.Events)).HandleAsync(new MoveEntries(Id, [P("y/Nota.md")], P("z")), Ct);

        (await v.ReadAsync("A.md")).Should().Be("[[x/Nota]]");
        (await v.ExistsAsync("z/Nota.md")).Should().BeTrue();
    }

    [Fact]
    [Trait("Spec", "F-002.5")]
    public async Task MoveByDrag_MovesAndUpdatesLinksIfNeeded()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "a"), ("B.md", "[[./A]]"), ("Archivo/x.md", "")));

        var moved = await new MoveEntriesHandler(v.Sessions, new EntryMover(v.Analyzer, v.Events)).HandleAsync(new MoveEntries(Id, [P("A.md")], P("Archivo")), Ct);

        moved.Value.Should().Equal(P("Archivo/A.md"));
        (await v.ReadAsync("B.md")).Should().Be("[[./Archivo/A]]");
    }

    [Fact]
    public async Task Move_WhenTheFileSystemMoveReportsAlreadyExists_MapsItAndRollsBack()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "a")));
        v.FileSystem.FailNextWrite(P("A.md"), new Error(ErrorCodes.FsAlreadyExists, "race"));

        var result = await new EntryMover(v.Analyzer, v.Events).MoveAsync(v.Session, [(P("A.md"), P("Z.md"))], Ct);

        result.Error.Code.Should().Be(ErrorCodes.PathAlreadyExists);
        (await v.ExistsAsync("A.md")).Should().BeTrue();
        (await v.ExistsAsync("Z.md")).Should().BeFalse();
    }

    [Fact]
    public async Task Move_WhenTheFileSystemMoveFails_ReturnsTheErrorAndRollsBack()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "a")));
        v.FileSystem.FailNextWrite(P("A.md"), new Error(ErrorCodes.FsIo, "disk"));

        var result = await new EntryMover(v.Analyzer, v.Events).MoveAsync(v.Session, [(P("A.md"), P("Z.md"))], Ct);

        result.Error.Code.Should().Be(ErrorCodes.FsIo);
        (await v.ExistsAsync("A.md")).Should().BeTrue();
        (await v.ExistsAsync("Z.md")).Should().BeFalse();
    }

    [Fact]
    public async Task Move_WhenALaterMoveFails_UndoesTheEarlierOnes()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "a"), ("C.md", "c")));
        v.FileSystem.FailNextWrite(P("C.md"), new Error(ErrorCodes.FsIo, "disk"));

        var result = await new EntryMover(v.Analyzer, v.Events).MoveAsync(
            v.Session, [(P("A.md"), P("B.md")), (P("C.md"), P("D.md"))], Ct);

        result.Error.Code.Should().Be(ErrorCodes.FsIo);
        (await v.ExistsAsync("A.md")).Should().BeTrue();
        (await v.ExistsAsync("B.md")).Should().BeFalse();
        (await v.ExistsAsync("C.md")).Should().BeTrue();
    }

    [Fact]
    public async Task Move_PublishesTheRenameAndTheRewrittenNotes()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "[[B]]"), ("B.md", "")));
        var renames = new List<EntryRenamed>();
        var saves = new List<NoteSaved>();
        using var renamed = v.Events.Subscribe<EntryRenamed>((e, _) => { renames.Add(e); return ValueTask.CompletedTask; });
        using var saved = v.Events.Subscribe<NoteSaved>((e, _) => { saves.Add(e); return ValueTask.CompletedTask; });

        await new EntryMover(v.Analyzer, v.Events).MoveAsync(v.Session, [(P("B.md"), P("C.md"))], Ct);

        renames.Select(e => (e.From, e.To)).Should().Equal((P("B.md"), P("C.md")));
        saves.Select(e => e.Path).Should().Equal(P("A.md"));
    }

    [Fact]
    public async Task Move_WithoutAnIndex_StillMovesTheFile()
    {
        await using var v = await VaultFixture.CreateAsync();
        var fs = new InMemoryVaultFileSystem(time: v.Clock.Time);
        await fs.WriteAllBytesAsync(P("A.md"), Encoding.UTF8.GetBytes("a"), null, Ct);
        await using var session = new VaultSession(Id, "Test", fs, VaultSettings.Default);

        var result = await new EntryMover(v.Analyzer, v.Events).MoveAsync(session, [(P("A.md"), P("B.md"))], Ct);

        result.IsSuccess.Should().BeTrue();
        (await fs.ExistsAsync(P("B.md"), Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Move_RejectsNullArguments()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "")));
        var mover = new EntryMover(v.Analyzer, v.Events);

        await FluentActions.Awaiting(() => mover.MoveAsync(null!, [(P("A.md"), P("B.md"))], Ct))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => mover.MoveAsync(v.Session, null!, Ct))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Move_WithAutoUpdateOff_DoesNotRewrite()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "[[B]]"), ("B.md", "")), VaultSettings.Default with { AutoUpdateLinks = false });

        await new RenameEntryHandler(v.Sessions, new EntryMover(v.Analyzer, v.Events)).HandleAsync(new RenameEntry(Id, P("B.md"), "C"), Ct);

        (await v.ReadAsync("A.md")).Should().Be("[[B]]");
    }

    [Fact]
    [Trait("Spec", "APP-5.2")]
    public async Task Rename_WhenALinkRewriteFails_RollsEverythingBack()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "[[B]]"), ("D.md", "[[B]]"), ("B.md", "")));
        v.FileSystem.FailNextWrite(P("D.md"), new Error(ErrorCodes.FsIo, "disk full"));

        var result = await new RenameEntryHandler(v.Sessions, new EntryMover(v.Analyzer, v.Events)).HandleAsync(new RenameEntry(Id, P("B.md"), "C"), Ct);

        result.Error.Code.Should().Be(ErrorCodes.FsIo);
        (await v.ReadAsync("A.md")).Should().Be("[[B]]");
        (await v.ReadAsync("D.md")).Should().Be("[[B]]");
        (await v.ExistsAsync("B.md")).Should().BeTrue();
        (await v.ExistsAsync("C.md")).Should().BeFalse();
    }

    [Fact]
    public async Task Move_FolderIntoItself_AndMissingEntries_Fail()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("X/a.md", "")));
        var mover = new MoveEntriesHandler(v.Sessions, new EntryMover(v.Analyzer, v.Events));

        (await mover.HandleAsync(new MoveEntries(Id, [P("X")], P("X/Sub")), Ct)).Error.Code.Should().Be(ErrorCodes.MoveIntoItself);
        (await mover.HandleAsync(new MoveEntries(Id, [P("nope.md")], P("X")), Ct)).Error.Code.Should().Be(ErrorCodes.EntryNotFound);
        (await mover.HandleAsync(new MoveEntries(Id, [P("X/a.md")], P("X")), Ct)).Value.Should().Equal(P("X/a.md"));
    }

    [Fact]
    [Trait("Spec", "F-002.6")]
    public async Task Delete_MovesToTheTrash_WithSuffixWhenTaken()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "first"), (".trash/A.md", "older")));

        (await new DeleteEntriesHandler(v.Sessions, v.Events).HandleAsync(new DeleteEntries(Id, [P("A.md")]), Ct)).IsSuccess.Should().BeTrue();
        await v.SettleAsync();

        (await v.ReadAsync(".trash/A 2.md")).Should().Be("first");
        (await v.ExistsAsync("A.md")).Should().BeFalse();
        v.Cache.Files.Should().NotContainKey(P("A.md"));
    }

    [Fact]
    public async Task Delete_PermanentAndFromTrash_AndSystemTrashFallback()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "a"), ("B.md", "b"), (".trash/old.md", "x")));
        var handler = new DeleteEntriesHandler(v.Sessions, v.Events);

        await handler.HandleAsync(new DeleteEntries(Id, [P("A.md")], DeleteMode.Permanent), Ct);
        await handler.HandleAsync(new DeleteEntries(Id, [P(".trash/old.md")]), Ct);
        await handler.HandleAsync(new DeleteEntries(Id, [P("B.md")], DeleteMode.SystemTrash), Ct);

        (await v.ExistsAsync("A.md")).Should().BeFalse();
        (await v.ExistsAsync(".trash/A.md")).Should().BeFalse();
        (await v.ExistsAsync(".trash/old.md")).Should().BeFalse();
        (await v.ReadAsync(".trash/B.md")).Should().Be("b");
        (await handler.HandleAsync(new DeleteEntries(Id, [P("none.md")]), Ct)).Error.Code.Should().Be(ErrorCodes.EntryNotFound);
    }

    [Fact]
    public async Task Delete_ToTheSystemTrash_WhenAvailable()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "a")));
        var trash = new FakeSystemTrash(v.FileSystem);

        (await new DeleteEntriesHandler(v.Sessions, v.Events, trash).HandleAsync(new DeleteEntries(Id, [P("A.md")], DeleteMode.SystemTrash), Ct)).IsSuccess.Should().BeTrue();

        trash.Trashed.Should().Equal(P("A.md"));
        (await v.ExistsAsync(".trash/A.md")).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_StopsAtTheFirstFailure_KeepingTheRest()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "a"), ("B.md", "b")));
        var trash = new FakeSystemTrash(v.FileSystem);
        v.FileSystem.FailNextWrite(P("B.md"), new Error(ErrorCodes.FsIo, "locked"));

        var result = await new DeleteEntriesHandler(v.Sessions, v.Events, trash).HandleAsync(new DeleteEntries(Id, [P("A.md"), P("B.md")], DeleteMode.SystemTrash), Ct);

        result.Error.Code.Should().Be(ErrorCodes.FsIo);
        (await v.ExistsAsync("A.md")).Should().BeFalse();
        (await v.ReadAsync("B.md")).Should().Be("b");
    }

    [Fact]
    public async Task FileTree_HidesInternalEntries_AndSortsFoldersFirst()
    {
        await using var v = await VaultFixture.CreateAsync(Files(
            ("b.md", ""), ("A.md", ""), ("Nota 10.md", ""), ("Nota 2.md", ""), ("img.png", ""), ("Z/x.md", ""), (".basalt/vault.json", "{}"), (".trash/t.md", "")));

        var tree = (await new GetFileTreeHandler(v.Sessions).HandleAsync(new GetFileTree(Id), Ct)).Value;

        tree.Children.Select(c => c.Path.ToString()).Should().Equal("Z", "A.md", "b.md", "img.png", "Nota 2.md", "Nota 10.md");
        tree.Children[0].IsFolder.Should().BeTrue();
        tree.Children[0].Children.Single().Kind.Should().Be(FileKind.Note);
        tree.Children.Single(c => c.Path == P("img.png")).Kind.Should().Be(FileKind.Image);

        var newest = (await new GetFileTreeHandler(v.Sessions).HandleAsync(new GetFileTree(Id, FileTreeSort.NameDescending), Ct)).Value;
        newest.Children.Select(c => c.Path.ToString()).First().Should().Be("Z");
        newest.Children.Select(c => c.Path.ToString()).Skip(1).First().Should().Be("Nota 10.md");
    }

    [Fact]
    [Trait("Spec", "F-003.5")]
    public async Task ImportAttachment_SavesInTheConfiguredFolder_AndReturnsAnEmbed()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("Notas/n.md", "")), VaultSettings.Default with { AttachmentFolder = "Adjuntos" });
        var handler = new ImportAttachmentHandler(v.Sessions, v.Events);

        var first = await handler.HandleAsync(new ImportAttachment(Id, P("Notas/n.md"), "Pasted image 20261019154233.png", [1, 2, 3]), Ct);
        var second = await handler.HandleAsync(new ImportAttachment(Id, P("Notas/n.md"), "Pasted image 20261019154233.png", [4]), Ct);

        first.Value.Path.Should().Be(P("Adjuntos/Pasted image 20261019154233.png"));
        first.Value.EmbedText.Should().Be("![[Pasted image 20261019154233.png]]");
        second.Value.Path.Should().Be(P("Adjuntos/Pasted image 20261019154233 1.png"));
    }

    [Fact]
    public async Task ImportAttachment_RelativeFolder_IsNextToTheNote()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("Notas/n.md", "")), VaultSettings.Default with { AttachmentFolder = "./img" });

        var imported = await new ImportAttachmentHandler(v.Sessions, v.Events).HandleAsync(new ImportAttachment(Id, P("Notas/n.md"), "a.png", [1]), Ct);

        imported.Value.Path.Should().Be(P("Notas/img/a.png"));
    }

    [Fact]
    [Trait("Spec", "F-010.1")]
    public async Task SetProperty_ChangesOnlyThatLine()
    {
        const string Text = "---\nstatus: draft\ntags: [a]\n---\n# Body\r\n";
        await using var v = await VaultFixture.CreateAsync(Files(("n.md", Text)));

        await new SetFrontmatterPropertyHandler(v.Sessions, v.FrontmatterEditor, v.Analyzer, v.Events)
            .HandleAsync(new SetFrontmatterProperty(Id, P("n.md"), "status", new StructuredValue.TextValue("done")), Ct);

        (await v.ReadAsync("n.md")).Should().Be(Text.Replace("draft", "done", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Spec", "F-010.2")]
    public async Task SetProperty_OnInvalidYaml_FailsAndLeavesTheFile()
    {
        const string Text = "---\nstatus: [unclosed\n---\nBody\n";
        await using var v = await VaultFixture.CreateAsync(Files(("n.md", Text)));

        var result = await new SetFrontmatterPropertyHandler(v.Sessions, v.FrontmatterEditor, v.Analyzer, v.Events)
            .HandleAsync(new SetFrontmatterProperty(Id, P("n.md"), "status", new StructuredValue.TextValue("done")), Ct);
        var removed = await new RemoveFrontmatterPropertyHandler(v.Sessions, v.FrontmatterEditor, v.Analyzer, v.Events)
            .HandleAsync(new RemoveFrontmatterProperty(Id, P("n.md"), "status"), Ct);

        result.Error.Code.Should().Be(ErrorCodes.NoteFrontmatterInvalid);
        removed.Error.Code.Should().Be(ErrorCodes.NoteFrontmatterInvalid);
        (await v.ReadAsync("n.md")).Should().Be(Text);
    }

    [Fact]
    public async Task RemoveProperty_Works()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("n.md", "---\na: 1\nb: 2\n---\n")));

        await new RemoveFrontmatterPropertyHandler(v.Sessions, v.FrontmatterEditor, v.Analyzer, v.Events).HandleAsync(new RemoveFrontmatterProperty(Id, P("n.md"), "a"), Ct);

        (await v.ReadAsync("n.md")).Should().Be("---\nb: 2\n---\n");
    }

    [Fact]
    public async Task ToggleTask_TogglesOnlyThatLine()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("t.md", "# T\n- [ ] uno\n  - [x] dos\n1. [ ] tres\ntexto")));
        var handler = new ToggleTaskHandler(v.Sessions, v.Events);

        await handler.HandleAsync(new ToggleTask(Id, P("t.md"), 1), Ct);
        await handler.HandleAsync(new ToggleTask(Id, P("t.md"), 2), Ct);
        await handler.HandleAsync(new ToggleTask(Id, P("t.md"), 3), Ct);

        (await v.ReadAsync("t.md")).Should().Be("# T\n- [x] uno\n  - [ ] dos\n1. [x] tres\ntexto");
        (await handler.HandleAsync(new ToggleTask(Id, P("t.md"), 4), Ct)).Error.Code.Should().Be(ErrorCodes.NoteNotATask);
        (await handler.HandleAsync(new ToggleTask(Id, P("t.md"), 99), Ct)).Error.Code.Should().Be(ErrorCodes.NoteNotATask);
    }

    [Fact]
    public async Task ToggleTask_WithAnOutOfRangeLine_ReportsThatItIsNotATask()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("t.md", "# T\n- [ ] uno")));
        var handler = new ToggleTaskHandler(v.Sessions, v.Events);

        var negative = await handler.HandleAsync(new ToggleTask(Id, P("t.md"), -1), Ct);
        var past = await handler.HandleAsync(new ToggleTask(Id, P("t.md"), 2), Ct);

        negative.Error.Code.Should().Be(ErrorCodes.NoteNotATask);
        past.Error.Code.Should().Be(ErrorCodes.NoteNotATask);
        negative.Error.Message.Should().Be("The line is not a task.");
    }

    [Fact]
    public async Task ToggleTask_WhenTheFileChangedConcurrently_ReportsItAndLeavesTheFile()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("t.md", "- [ ] uno")));
        v.FileSystem.FailNextWrite(P("t.md"), new Error(ErrorCodes.FsConcurrentModification, "changed"));

        var result = await new ToggleTaskHandler(v.Sessions, v.Events).HandleAsync(new ToggleTask(Id, P("t.md"), 0), Ct);

        result.Error.Code.Should().Be(ErrorCodes.NoteModifiedExternally);
        result.Error.Message.Should().Be("The note changed on disk; try again.");
        (await v.ReadAsync("t.md")).Should().Be("- [ ] uno");
    }

    [Fact]
    public async Task ToggleTask_WhenTheWriteFails_ReturnsTheRawError()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("t.md", "- [ ] uno")));
        v.FileSystem.FailNextWrite(P("t.md"), new Error(ErrorCodes.FsIo, "disk"));

        var result = await new ToggleTaskHandler(v.Sessions, v.Events).HandleAsync(new ToggleTask(Id, P("t.md"), 0), Ct);

        result.Error.Code.Should().Be(ErrorCodes.FsIo);
    }

    [Fact]
    public async Task ToggleTask_OnAMissingNote_Fails()
    {
        await using var v = await VaultFixture.CreateAsync();

        var result = await new ToggleTaskHandler(v.Sessions, v.Events).HandleAsync(new ToggleTask(Id, P("none.md"), 0), Ct);

        result.Error.Code.Should().Be(ErrorCodes.EntryNotFound);
    }

    [Fact]
    public async Task ToggleTask_PublishesNoteSaved()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("t.md", "- [ ] uno")));
        var saved = new List<NoteSaved>();
        using var subscription = v.Events.Subscribe<NoteSaved>((e, _) => { saved.Add(e); return ValueTask.CompletedTask; });

        await new ToggleTaskHandler(v.Sessions, v.Events).HandleAsync(new ToggleTask(Id, P("t.md"), 0), Ct);
        await v.Events.WaitForIdleAsync(Ct);

        saved.Select(e => e.Path).Should().Equal(P("t.md"));
    }

    [Fact]
    public async Task EditHandlers_RejectNullCommandsAndClosedVaults()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("t.md", "- [ ] uno")));
        var closed = new VaultId(Guid.Parse("00000000-0000-0000-0000-0000000000ff"));
        var toggle = new ToggleTaskHandler(v.Sessions, v.Events);
        var set = new SetFrontmatterPropertyHandler(v.Sessions, v.FrontmatterEditor, v.Analyzer, v.Events);
        var remove = new RemoveFrontmatterPropertyHandler(v.Sessions, v.FrontmatterEditor, v.Analyzer, v.Events);

        await FluentActions.Awaiting(() => toggle.HandleAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => set.HandleAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => remove.HandleAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();

        (await toggle.HandleAsync(new ToggleTask(closed, P("t.md"), 0), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
        (await set.HandleAsync(new SetFrontmatterProperty(closed, P("t.md"), "a", new StructuredValue.TextValue("b")), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
        (await remove.HandleAsync(new RemoveFrontmatterProperty(closed, P("t.md"), "a"), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
    }

    [Fact]
    public async Task ImportAttachment_RejectsNestedAndInvalidNames()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("Notas/n.md", "")));
        var handler = new ImportAttachmentHandler(v.Sessions, v.Events);

        var nested = await handler.HandleAsync(new ImportAttachment(Id, P("Notas/n.md"), "sub/a.png", [1]), Ct);
        var invalid = await handler.HandleAsync(new ImportAttachment(Id, P("Notas/n.md"), "bad?.png", [1]), Ct);

        nested.Error.Code.Should().Be(ErrorCodes.PathAlreadyExists);
        invalid.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task ImportAttachment_PublishesEntriesCreated()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("Notas/n.md", "")));
        var created = new List<EntriesCreated>();
        using var subscription = v.Events.Subscribe<EntriesCreated>((e, _) => { created.Add(e); return ValueTask.CompletedTask; });

        await new ImportAttachmentHandler(v.Sessions, v.Events)
            .HandleAsync(new ImportAttachment(Id, P("Notas/n.md"), "a.png", [1]), Ct);
        await v.Events.WaitForIdleAsync(Ct);

        created.Single().Paths.Should().Equal(P("a.png"));
    }

    [Fact]
    public async Task ImportAttachment_RejectsNullCommandsAndClosedVaults()
    {
        await using var v = await VaultFixture.CreateAsync();
        var handler = new ImportAttachmentHandler(v.Sessions, v.Events);
        var closed = new VaultId(Guid.Parse("00000000-0000-0000-0000-0000000000ff"));

        await FluentActions.Awaiting(() => handler.HandleAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
        (await handler.HandleAsync(new ImportAttachment(closed, P("n.md"), "a.png", [1]), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
    }

    [Fact]
    public async Task CreateNote_UsesTheRootWhenTheDefaultFolderIsInvalid()
    {
        await using var v = await VaultFixture.CreateAsync(Files(), VaultSettings.Default with { NewNoteFolder = "bad?" });
        var handler = new CreateNoteHandler(v.Sessions, v.Events, v.Clock, v.Culture);

        var created = await handler.HandleAsync(new CreateNote(Id), Ct);

        created.Value.Should().Be(P("Untitled.md"));
    }

    [Fact]
    public async Task CreateNote_WhenTheFileAppearsWhileCreating_ReportsAlreadyExists()
    {
        await using var v = await VaultFixture.CreateAsync();
        v.FileSystem.FailNextWrite(P("Untitled.md"), new Error(ErrorCodes.FsAlreadyExists, "race"));

        var result = await new CreateNoteHandler(v.Sessions, v.Events, v.Clock, v.Culture).HandleAsync(new CreateNote(Id), Ct);

        result.Error.Code.Should().Be(ErrorCodes.PathAlreadyExists);
    }

    [Fact]
    public async Task CreateNote_PropagatesOtherCreateErrorsAndPublishesTheEntry()
    {
        await using var v = await VaultFixture.CreateAsync();
        var created = new List<EntriesCreated>();
        using var subscription = v.Events.Subscribe<EntriesCreated>((e, _) => { created.Add(e); return ValueTask.CompletedTask; });
        var handler = new CreateNoteHandler(v.Sessions, v.Events, v.Clock, v.Culture);

        (await handler.HandleAsync(new CreateNote(Id, Title: "A"), Ct)).IsSuccess.Should().BeTrue();
        await v.Events.WaitForIdleAsync(Ct);
        v.FileSystem.FailNextWrite(P("B.md"), new Error(ErrorCodes.FsIo, "disk"));
        var failed = await handler.HandleAsync(new CreateNote(Id, Title: "B"), Ct);

        created.SelectMany(e => e.Paths).Should().Equal(P("A.md"));
        failed.Error.Code.Should().Be(ErrorCodes.FsIo);
        await FluentActions.Awaiting(() => handler.HandleAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CreateFolder_CreatesAndPublishesOrFails()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "")));
        var created = new List<EntriesCreated>();
        using var subscription = v.Events.Subscribe<EntriesCreated>((e, _) => { created.Add(e); return ValueTask.CompletedTask; });
        var handler = new CreateFolderHandler(v.Sessions, v.Events);

        (await handler.HandleAsync(new CreateFolder(Id, P("Nueva")), Ct)).Value.Should().Be(P("Nueva"));
        (await handler.HandleAsync(new CreateFolder(Id, P("Nueva")), Ct)).Error.Code.Should().Be(ErrorCodes.PathAlreadyExists);
        (await handler.HandleAsync(new CreateFolder(Id, VaultPath.Root), Ct)).Error.Code.Should().Be(ErrorCodes.PathAlreadyExists);
        await v.Events.WaitForIdleAsync(Ct);
        (await handler.HandleAsync(new CreateFolder(new VaultId(Guid.Parse("00000000-0000-0000-0000-0000000000ff")), P("X")), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
        await FluentActions.Awaiting(() => handler.HandleAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
        created.SelectMany(e => e.Paths).Should().Equal(P("Nueva"));
    }

    [Fact]
    [Trait("Spec", "F-002.7")]
    public async Task ExternalCreation_AppearsInTheTreeAndIsIndexed()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "")));

        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = v.Events.Subscribe<VaultFileChangedExternally>((_, _) =>
        {
            delivered.TrySetResult();
            return ValueTask.CompletedTask;
        });

        v.FileSystem.SimulateExternalWrite(P("Externa.md"), "#nueva");
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await v.SettleAsync();

        var tree = (await new GetFileTreeHandler(v.Sessions).HandleAsync(new GetFileTree(Id), Ct)).Value;
        tree.Children.Select(c => c.Path).Should().Contain(P("Externa.md"));
        v.Cache.Files[P("Externa.md")].Metadata.Tags.Select(t => t.Tag.ToString()).Should().Equal("#nueva");
    }

    [Fact]
    public async Task FileTree_SortsByModificationTime()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("old.md", "")));
        v.Clock.Advance(TimeSpan.FromMinutes(5));
        await v.FileSystem.WriteAllBytesAsync(P("new.md"), Array.Empty<byte>(), null, Ct);
        var handler = new GetFileTreeHandler(v.Sessions);

        (await handler.HandleAsync(new GetFileTree(Id, FileTreeSort.ModifiedNewest), Ct)).Value.Children.Select(c => c.Path.ToString()).Should().Equal("new.md", "old.md");
        (await handler.HandleAsync(new GetFileTree(Id, FileTreeSort.ModifiedOldest), Ct)).Value.Children.Select(c => c.Path.ToString()).Should().Equal("old.md", "new.md");
    }

    [Fact]
    public async Task Delete_WithNoPaths_DoesNothingAndPublishesNothing()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "")));
        var deleted = new List<EntriesDeleted>();
        using var subscription = v.Events.Subscribe<EntriesDeleted>((e, _) => { deleted.Add(e); return ValueTask.CompletedTask; });

        var result = await new DeleteEntriesHandler(v.Sessions, v.Events).HandleAsync(new DeleteEntries(Id, []), Ct);
        await v.Events.WaitForIdleAsync(Ct);

        result.IsSuccess.Should().BeTrue();
        deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_WhenOneMoveFails_PublishesTheOnesAlreadyDeleted()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "a"), ("B.md", "b")));
        var deleted = new List<EntriesDeleted>();
        using var subscription = v.Events.Subscribe<EntriesDeleted>((e, _) => { deleted.Add(e); return ValueTask.CompletedTask; });
        v.FileSystem.FailNextWrite(P("A.md"), new Error(ErrorCodes.FsIo, "disk"));

        var result = await new DeleteEntriesHandler(v.Sessions, v.Events)
            .HandleAsync(new DeleteEntries(Id, [P("B.md"), P("A.md")]), Ct);
        await v.Events.WaitForIdleAsync(Ct);

        result.Error.Code.Should().Be(ErrorCodes.FsIo);
        deleted.SelectMany(e => e.Paths).Should().Equal(P("B.md"));
        (await v.ExistsAsync("B.md")).Should().BeFalse();
        (await v.ExistsAsync("A.md")).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_ToAnUnavailableSystemTrash_FallsBackToTheVaultTrash()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "")));
        var trash = Substitute.For<ISystemTrash>();
        trash.IsAvailable.Returns(false);

        await new DeleteEntriesHandler(v.Sessions, v.Events, trash)
            .HandleAsync(new DeleteEntries(Id, [P("A.md")], DeleteMode.SystemTrash), Ct);

        (await v.ExistsAsync(".trash/A.md")).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAndMove_RejectNullsAndClosedVaults()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "")));
        var closed = new VaultId(Guid.Parse("00000000-0000-0000-0000-0000000000ff"));
        var delete = new DeleteEntriesHandler(v.Sessions, v.Events);
        var move = new MoveEntriesHandler(v.Sessions, new EntryMover(v.Analyzer, v.Events));

        await FluentActions.Awaiting(() => delete.HandleAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => move.HandleAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
        (await delete.HandleAsync(new DeleteEntries(closed, [P("A.md")]), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
        (await move.HandleAsync(new MoveEntries(closed, [P("A.md")], P("X")), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
    }

    [Fact]
    public async Task Move_WhenEveryPathIsAlreadyInTheTarget_ReturnsThemUnchanged()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("X/A.md", "")));

        var result = await new MoveEntriesHandler(v.Sessions, new EntryMover(v.Analyzer, v.Events))
            .HandleAsync(new MoveEntries(Id, [P("X/A.md")], P("X")), Ct);

        result.Value.Should().Equal(P("X/A.md"));
    }

    [Fact]
    public async Task Rename_ADirectoryKeepsItWithoutAnExtension()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("Dir.old/A.md", "")));

        var result = await new RenameEntryHandler(v.Sessions, new EntryMover(v.Analyzer, v.Events))
            .HandleAsync(new RenameEntry(Id, P("Dir.old"), "Dir.new"), Ct);

        result.Value.Should().Be(P("Dir.new"));
        (await v.ExistsAsync("Dir.new/A.md")).Should().BeTrue();
    }

    [Fact]
    public async Task Rename_KeepsTheExtension_IgnoresNoOps_AndRejectsMissingOrInvalid()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("img.png", "x"), ("n.md", "")));
        var handler = new RenameEntryHandler(v.Sessions, new EntryMover(v.Analyzer, v.Events));

        (await handler.HandleAsync(new RenameEntry(Id, P("img.png"), "foto"), Ct)).Value.Should().Be(P("foto.png"));
        (await handler.HandleAsync(new RenameEntry(Id, P("n.md"), "n.md"), Ct)).Value.Should().Be(P("n.md"));
        (await handler.HandleAsync(new RenameEntry(Id, P("none.md"), "x"), Ct)).Error.Code.Should().Be(ErrorCodes.EntryNotFound);
        (await handler.HandleAsync(new RenameEntry(Id, P("n.md"), "a/b"), Ct)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Move_OntoAnExistingEntry_FailsWithoutChanges()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "1"), ("X/A.md", "2")));

        var result = await new MoveEntriesHandler(v.Sessions, new EntryMover(v.Analyzer, v.Events)).HandleAsync(new MoveEntries(Id, [P("A.md")], P("X")), Ct);

        result.Error.Code.Should().Be(ErrorCodes.PathAlreadyExists);
        (await v.ReadAsync("A.md")).Should().Be("1");
        (await v.ReadAsync("X/A.md")).Should().Be("2");
    }

    [Fact]
    public async Task Save_WhenTheNoteWasDeletedMeanwhile_ReportsAnExternalChange()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("n.md", "x")));
        var sha = (await new ReadNoteHandler(v.Sessions, v.Analyzer).HandleAsync(new ReadNote(Id, P("n.md")), Ct)).Value.Sha;
        v.FileSystem.SimulateExternalDelete(P("n.md"));

        var result = await new SaveNoteHandler(v.Sessions, v.Events).HandleAsync(new SaveNote(Id, P("n.md"), "y", sha), Ct);

        result.Error.Code.Should().Be(ErrorCodes.NoteModifiedExternally);
        result.Error.Metadata!["currentText"].Should().BeNull();
    }

    [Fact]
    public async Task Save_PropagatesWriteFailures()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("n.md", "x")));
        v.FileSystem.FailNextWrite(P("n.md"), new Error(ErrorCodes.FsIo, "disk full"));

        var result = await new SaveNoteHandler(v.Sessions, v.Events).HandleAsync(new SaveNote(Id, P("n.md"), "y", null), Ct);

        result.Error.Code.Should().Be(ErrorCodes.FsIo);
        (await v.ReadAsync("n.md")).Should().Be("x");
    }

    [Fact]
    public async Task Edits_WithoutChanges_DoNotWrite_AndMissingNotesFail()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("n.md", "---\na: 1\n---\n")));
        var handler = new RemoveFrontmatterPropertyHandler(v.Sessions, v.FrontmatterEditor, v.Analyzer, v.Events);
        v.FileSystem.FailNextWrite(P("n.md"), new Error(ErrorCodes.FsIo, "would fail if written"));

        (await handler.HandleAsync(new RemoveFrontmatterProperty(Id, P("n.md"), "missing"), Ct)).IsSuccess.Should().BeTrue();
        (await handler.HandleAsync(new RemoveFrontmatterProperty(Id, P("none.md"), "a"), Ct)).Error.Code.Should().Be(ErrorCodes.EntryNotFound);
        (await handler.HandleAsync(new RemoveFrontmatterProperty(Id, P("n.md"), "a"), Ct)).Error.Code.Should().Be(ErrorCodes.FsIo);
    }

    [Fact]
    public async Task ImportAttachment_RejectsNamesWithFolders()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("n.md", "")));
        var handler = new ImportAttachmentHandler(v.Sessions, v.Events);

        (await handler.HandleAsync(new ImportAttachment(Id, P("n.md"), "a/b.png", [1]), Ct)).IsFailure.Should().BeTrue();
        (await handler.HandleAsync(new ImportAttachment(Id, P("n.md"), "a:b.png", [1]), Ct)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Handlers_RequireAnOpenVault()
    {
        await using var v = await VaultFixture.CreateAsync();
        var closed = new VaultId(Guid.Parse("00000000-0000-0000-0000-0000000000ff"));

        (await new CreateNoteHandler(v.Sessions, v.Events, v.Clock, v.Culture).HandleAsync(new CreateNote(closed), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
        (await new SaveNoteHandler(v.Sessions, v.Events).HandleAsync(new SaveNote(closed, P("a.md"), "", null), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
        (await new GetFileTreeHandler(v.Sessions).HandleAsync(new GetFileTree(closed), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
        (await new ToggleTaskHandler(v.Sessions, v.Events).HandleAsync(new ToggleTask(closed, P("a.md"), 0), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
    }

    private static async Task<List<string>> ListAsync(VaultFixture v)
    {
        var result = new List<string>();
        await foreach (var entry in v.FileSystem.EnumerateAsync(VaultPath.Root, true, Ct))
        {
            result.Add(entry.Path.ToString());
        }

        return result;
    }
}



