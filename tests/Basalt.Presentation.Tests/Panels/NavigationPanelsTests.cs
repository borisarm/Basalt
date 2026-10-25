using Basalt.Application.Abstractions;
using Basalt.Application.Bookmarks;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Index;
using Basalt.Application.Search;
using Basalt.Application.Tags;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Search;
using Basalt.Domain.Tags;
using Basalt.Presentation.Common;
using Basalt.Presentation.Panels;
using Basalt.Presentation.Workspace;
using NSubstitute;

namespace Basalt.Presentation.Tests.Panels;

public sealed class FileExplorerViewModelTests : PanelTestBase
{
    private readonly IQueryHandler<GetFileTree, FileTreeNode> _tree = Substitute.For<IQueryHandler<GetFileTree, FileTreeNode>>();
    private readonly ICommandHandler<CreateNote, VaultPath> _createNote = Substitute.For<ICommandHandler<CreateNote, VaultPath>>();
    private readonly ICommandHandler<CreateFolder, VaultPath> _createFolder = Substitute.For<ICommandHandler<CreateFolder, VaultPath>>();
    private readonly ICommandHandler<RenameEntry, VaultPath> _rename = Substitute.For<ICommandHandler<RenameEntry, VaultPath>>();
    private readonly ICommandHandler<DeleteEntries, Unit> _delete = Substitute.For<ICommandHandler<DeleteEntries, Unit>>();
    private readonly FileExplorerViewModel _vm;

    private static FileTreeNode Node(string path, params FileTreeNode[] children) =>
        new(path.Length == 0 ? VaultPath.Root : P(path), children.Length > 0 || !path.Contains('.', StringComparison.Ordinal), path.EndsWith(".md", StringComparison.Ordinal) ? FileKind.Note : FileKind.Other, default, children);

    public FileExplorerViewModelTests()
    {
        _tree.HandleAsync(Arg.Any<GetFileTree>(), Arg.Any<CancellationToken>()).Returns(Node(string.Empty, Node("Proyectos", Node("Proyectos/X.md")), Node("A.md"), Node("img.png")));
        _vm = new FileExplorerViewModel(Services, Context, new FileExplorerUseCases(_tree, _createNote, _createFolder, _rename, _delete), Dialogs);
    }

    [Fact]
    public async Task Load_BuildsTheTree_WithNoteNamesWithoutExtension()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);

        _vm.Roots.Select(n => (n.Name, n.IsFolder)).Should().Equal(("Proyectos", true), ("A", false), ("img.png", false));
        _vm.Roots[0].Children.Single().Name.Should().Be("X");
    }

    [Fact]
    [Trait("Spec", "F-002")]
    public async Task Nodes_TellNotesApartFromFoldersAndAttachments()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);

        _vm.Roots.Select(n => (n.Name, n.IsNote)).Should().Equal(("Proyectos", false), ("A", true), ("img.png", false));
    }

    [Fact]
    [Trait("Spec", "F-002.1")]
    public async Task NewNote_InTheSelectedFolder_IsOpened()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        _createNote.HandleAsync(Arg.Any<CreateNote>(), Arg.Any<CancellationToken>()).Returns(P("Proyectos/Untitled.md"));

        await _vm.NewNoteCommand.ExecuteAsync(_vm.Roots[0]);

        await _createNote.Received(1).HandleAsync(new CreateNote(Vault, P("Proyectos")), Arg.Any<CancellationToken>());
        await Navigator.Received(1).OpenAsync(new OpenRequest(P("Proyectos/Untitled.md")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenRenameAndDelete()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        var a = _vm.Roots[1];
        _rename.HandleAsync(Arg.Any<RenameEntry>(), Arg.Any<CancellationToken>()).Returns(P("B.md"));
        _delete.HandleAsync(Arg.Any<DeleteEntries>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        Dialogs.PromptAnswer = "B";

        await _vm.OpenCommand.ExecuteAsync(a);
        await _vm.RenameCommand.ExecuteAsync(a);
        await _vm.DeleteCommand.ExecuteAsync(a);

        await Navigator.Received(1).OpenAsync(new OpenRequest(P("A.md")), Arg.Any<CancellationToken>());
        await _rename.Received(1).HandleAsync(new RenameEntry(Vault, P("A.md"), "B"), Arg.Any<CancellationToken>());
        await _delete.Received(1).HandleAsync(Arg.Is<DeleteEntries>(d => d.Paths.Single() == P("A.md") && d.Mode == DeleteMode.Trash), Arg.Any<CancellationToken>());
        Dialogs.Requests.OfType<ConfirmRequest>().Single().IsDestructive.Should().BeTrue();
    }

    [Fact]
    public async Task NewFolder_AndCancelledPrompts()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);
        _createFolder.HandleAsync(Arg.Any<CreateFolder>(), Arg.Any<CancellationToken>()).Returns(P("Nueva"));
        Dialogs.PromptAnswer = null;
        await _vm.NewFolderCommand.ExecuteAsync(null);
        await _vm.RenameCommand.ExecuteAsync(_vm.Roots[1]);

        Dialogs.PromptAnswer = "Nueva";
        await _vm.NewFolderCommand.ExecuteAsync(null);

        await _createFolder.Received(1).HandleAsync(new CreateFolder(Vault, P("Nueva")), Arg.Any<CancellationToken>());
        await _rename.DidNotReceiveWithAnyArgs().HandleAsync(default!, Ct);
    }

    [Fact]
    public async Task FileEvents_RefreshTheTree_AndRevealSelects()
    {
        await _vm.RefreshCommand.ExecuteAsync(null);

        await Events.PublishAsync(new EntriesCreated(Vault, [P("Nuevo.md")]), Ct);
        await Events.WaitForIdleAsync(Ct);
        Messenger.Send(new RevealInExplorerRequested(P("Proyectos/X.md")));

        await _tree.Received(2).HandleAsync(Arg.Any<GetFileTree>(), Arg.Any<CancellationToken>());
        _vm.Roots[0].IsExpanded.Should().BeTrue();
        _vm.Selected!.Path.Should().Be(P("Proyectos/X.md"));
    }
}

public sealed class SearchPanelViewModelTests : PanelTestBase
{
    private readonly IQueryHandler<SearchNotes, IReadOnlyList<SearchHit>> _search = Substitute.For<IQueryHandler<SearchNotes, IReadOnlyList<SearchHit>>>();
    private readonly SearchPanelViewModel _vm;

    public SearchPanelViewModelTests()
    {
        _search.HandleAsync(Arg.Any<SearchNotes>(), Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<SearchHit>>(
        [
            new SearchHit(P("A.md"), FileKind.Note, default, [new MatchRange(MatchTarget.Content, 0, 3), new MatchRange(MatchTarget.Content, 9, 3)], 2),
            new SearchHit(P("img.png"), FileKind.Image, default, [new MatchRange(MatchTarget.FileName, 0, 3)], 11),
        ]));
        _vm = new SearchPanelViewModel(Services, Context, _search);
    }

    [Fact]
    [Trait("Spec", "F-007.1")]
    public async Task Typing_SearchesAfterADebounce()
    {
        _vm.Query = "reu";
        _vm.Query = "reunión";
        await _search.DidNotReceiveWithAnyArgs().HandleAsync(default!, Ct);

        Time.Advance(SearchPanelViewModel.Debounce);
        await _vm.WhenSearchedAsync();

        await _search.Received(1).HandleAsync(Arg.Is<SearchNotes>(s => s.Query == "reunión"), Arg.Any<CancellationToken>());
        _vm.Results.Select(r => (r.Title, r.MatchCount)).Should().Equal(("A", 2), ("img.png", 1));
        _vm.Error.Should().BeNull();
    }

    [Fact]
    [Trait("Spec", "F-007.4")]
    public async Task InvalidQueries_ShowAnInlineError()
    {
        _search.HandleAsync(Arg.Any<SearchNotes>(), Arg.Any<CancellationToken>()).Returns(new Error(DomainErrors.SearchInvalidRegex, "x"));

        _vm.Query = "/[a-/";
        Time.Advance(SearchPanelViewModel.Debounce);
        await _vm.WhenSearchedAsync();

        _vm.Error.Should().Be("The regular expression is not valid.");
        _vm.Results.Should().BeEmpty();
        Notifications.Shown.Should().BeEmpty();
    }

    [Fact]
    public async Task Options_ReachTheQuery_AndOpeningAResult()
    {
        _vm.MatchCase = true;
        _vm.Sort = SearchSort.ModifiedNewest;
        _vm.Query = "x";
        Time.Advance(SearchPanelViewModel.Debounce);
        await _vm.WhenSearchedAsync();

        await _search.Received(1).HandleAsync(Arg.Is<SearchNotes>(s => s.Options!.MatchCase && s.Options.Sort == SearchSort.ModifiedNewest), Arg.Any<CancellationToken>());
        await _vm.OpenCommand.ExecuteAsync(_vm.Results[0]);
        await Navigator.Received(1).OpenAsync(new OpenRequest(P("A.md")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchRequests_FromOtherPanels_RunImmediately()
    {
        Messenger.Send(new SearchRequested("tag:#trabajo"));
        await _vm.WhenSearchedAsync();

        _vm.Query.Should().Be("tag:#trabajo");
        await _search.Received(1).HandleAsync(Arg.Is<SearchNotes>(s => s.Query == "tag:#trabajo"), Arg.Any<CancellationToken>());
        Navigator.Received(1).FocusPanel(PanelIds.Search);
    }

    [Fact]
    public async Task EmptyQuery_ClearsResults()
    {
        _vm.Query = "x";
        Time.Advance(SearchPanelViewModel.Debounce);
        await _vm.WhenSearchedAsync();

        _vm.Query = "  ";
        Time.Advance(SearchPanelViewModel.Debounce);
        await _vm.WhenSearchedAsync();

        _vm.Results.Should().BeEmpty();
        await _search.Received(1).HandleAsync(Arg.Any<SearchNotes>(), Arg.Any<CancellationToken>());
    }
}

public sealed class BookmarksAndTagsTests : PanelTestBase
{
    private readonly IQueryHandler<ListBookmarks, BookmarksView> _list = Substitute.For<IQueryHandler<ListBookmarks, BookmarksView>>();
    private readonly ICommandHandler<RemoveBookmark, Unit> _remove = Substitute.For<ICommandHandler<RemoveBookmark, Unit>>();
    private readonly IQueryHandler<GetTags, TagHierarchy> _tags = Substitute.For<IQueryHandler<GetTags, TagHierarchy>>();

    [Fact]
    public async Task Bookmarks_OpenFilesHeadingsAndSearches()
    {
        _list.HandleAsync(Arg.Any<ListBookmarks>(), Arg.Any<CancellationToken>()).Returns(new BookmarksView(new BookmarksDocument(
        [
            Bookmark.ForFile("A.md", "#Tema"),
            Bookmark.ForGroup("Trabajo", Bookmark.ForSearch("tag:#x", "Urgente"), Bookmark.ForFolder("Clientes")),
        ]), false, null));
        var vm = new BookmarksViewModel(Services, Context, _list, _remove);
        SearchRequested? search = null;
        RevealInExplorerRequested? reveal = null;
        Messenger.Register<SearchRequested>(this, (_, m) => search = m);
        Messenger.Register<RevealInExplorerRequested>(this, (_, m) => reveal = m);

        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.OpenCommand.ExecuteAsync(vm.Items[0]);
        await vm.OpenCommand.ExecuteAsync(vm.Items[1].Children[0]);
        await vm.OpenCommand.ExecuteAsync(vm.Items[1].Children[1]);

        vm.Items.Select(i => i.Title).Should().Equal("A › Tema", "Trabajo");
        vm.Items[1].Children.Select(i => i.Title).Should().Equal("Urgente", "Clientes");
        await Navigator.Received(1).OpenAsync(new OpenRequest(P("A.md"), Subpath: "#Tema"), Arg.Any<CancellationToken>());
        search!.Query.Should().Be("tag:#x");
        reveal!.Path.Should().Be(P("Clientes"));
    }

    [Fact]
    public async Task Bookmarks_InvalidFile_ShowsAWarning_AndRemoveReloads()
    {
        _list.HandleAsync(Arg.Any<ListBookmarks>(), Arg.Any<CancellationToken>()).Returns(new BookmarksView(new BookmarksDocument([Bookmark.ForFile("A.md")]), true, "bad"));
        _remove.HandleAsync(Arg.Any<RemoveBookmark>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var vm = new BookmarksViewModel(Services, Context, _list, _remove);

        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RemoveCommand.ExecuteAsync(vm.Items[0]);

        vm.Warning.Should().Be("The bookmarks file could not be read; it will be kept aside when you change a bookmark.");
        await _remove.Received(1).HandleAsync(new RemoveBookmark(Vault, Bookmark.ForFile("A.md")), Arg.Any<CancellationToken>());
        await _list.Received(2).HandleAsync(Arg.Any<ListBookmarks>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Spec", "F-006.1")]
    public async Task Tags_ShowTheTree_AndClickingSearches()
    {
        _tags.HandleAsync(Arg.Any<GetTags>(), Arg.Any<CancellationToken>()).Returns(TagHierarchy.Build([Tag.Create("trabajo/cliente-a").Value, Tag.Create("trabajo").Value]));
        var vm = new TagsPanelViewModel(Services, Context, _tags);
        SearchRequested? search = null;
        Messenger.Register<SearchRequested>(this, (_, m) => search = m);

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SearchTagCommand.Execute(vm.Roots[0].Children[0]);

        vm.Roots.Single().Should().BeEquivalentTo(new { Name = "trabajo", Count = 2 });
        vm.Roots[0].Children.Single().Should().BeEquivalentTo(new { Name = "cliente-a", Count = 1 });
        search!.Query.Should().Be("tag:#trabajo/cliente-a");

        await Events.PublishAsync(new MetadataChanged(Vault, [P("a.md")]), Ct);
        await Events.WaitForIdleAsync(Ct);
        await _tags.Received(2).HandleAsync(Arg.Any<GetTags>(), Arg.Any<CancellationToken>());
    }
}
