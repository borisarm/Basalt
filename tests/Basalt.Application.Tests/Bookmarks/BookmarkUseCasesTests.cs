using Basalt.Application.Bookmarks;
using Basalt.Application.Errors;
using Basalt.Application.Notes;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Tests.TestSupport;
using Basalt.Application.Workspace;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;
using NSubstitute;
using static Basalt.Application.Tests.TestSupport.VaultFixture;

namespace Basalt.Application.Tests.Bookmarks;

public sealed class BookmarkUseCasesTests
{
    private static readonly VaultId Closed = new(Guid.Parse("00000000-0000-0000-0000-0000000000ff"));

    private static Dictionary<string, string> Files(params (string Path, string Text)[] files) =>
        files.ToDictionary(f => f.Path, f => f.Text);

    private static async Task<VaultFixture> CreateAsync(params (string Path, string Text)[] files)
    {
        var v = await VaultFixture.CreateAsync(Files(files));
        await new ProductivitySessionInitializer(new InMemoryVaultConfigStore(), new InMemoryRecentFilesStore(), v.Events)
            .InitializeAsync(v.Session, Ct);
        return v;
    }

    private static IVaultConfigStore Store(BookmarksDocument document)
    {
        var store = Substitute.For<IVaultConfigStore>();
        store.LoadBookmarksAsync(Arg.Any<IVaultFileSystem>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentLoad<BookmarksDocument>(document, DocumentLoadStatus.Loaded));
        store.SaveBookmarksAsync(Arg.Any<IVaultFileSystem>(), Arg.Any<BookmarksDocument>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        return store;
    }

    private static async Task<BookmarksDocument> LoadAsync(VaultFixture v) =>
        (await v.Session.Get<BookmarkList>().LoadAsync(Ct)).Value;

    [Fact]
    public async Task Update_WhenTheChangeFails_ReturnsTheErrorWithoutSaving()
    {
        var store = Store(BookmarksDocument.Empty);
        var list = new BookmarkList((await CreateAsync()).Session, store);
        var failure = new Error("bookmark.invalid", "nope");

        var result = await list.UpdateAsync(_ => Result<BookmarksDocument?>.Failure(failure), Ct);

        result.Error.Should().Be(failure);
        await store.DidNotReceive().SaveBookmarksAsync(Arg.Any<IVaultFileSystem>(), Arg.Any<BookmarksDocument>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_WhenTheChangeReturnsNull_DoesNotSave()
    {
        var store = Store(BookmarksDocument.Empty);
        var list = new BookmarkList((await CreateAsync()).Session, store);

        var result = await list.UpdateAsync(_ => Result.Success<BookmarksDocument?>(null), Ct);

        result.IsSuccess.Should().BeTrue();
        await store.DidNotReceive().SaveBookmarksAsync(Arg.Any<IVaultFileSystem>(), Arg.Any<BookmarksDocument>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_PropagatesSaveFailures()
    {
        var failure = new Error("store.io", "disk full");
        var store = Store(BookmarksDocument.Empty);
        store.SaveBookmarksAsync(Arg.Any<IVaultFileSystem>(), Arg.Any<BookmarksDocument>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit>.Failure(failure));
        var list = new BookmarkList((await CreateAsync()).Session, store);

        var result = await list.UpdateAsync(_ => Result.Success<BookmarksDocument?>(new BookmarksDocument([Bookmark.ForFile("A.md")])), Ct);

        result.Error.Should().Be(failure);
    }

    [Fact]
    public async Task Update_RejectsNullChanges()
    {
        await using var v = await CreateAsync();

        await FluentActions.Awaiting(() => v.Session.Get<BookmarkList>().UpdateAsync(null!, Ct))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Rename_MovesBookmarksInsideTheRenamedFolderAndItsGroups()
    {
        await using var v = await CreateAsync(("Carpeta/X.md", ""), ("Otra/Y.md", ""));

        await new AddBookmarkHandler(v.Sessions).HandleAsync(
            new AddBookmark(Id, Bookmark.ForGroup("G", Bookmark.ForFile("Carpeta/X.md"), Bookmark.ForFolder("Carpeta"), Bookmark.ForFile("Otra/Y.md"))), Ct);
        await new AddBookmarkHandler(v.Sessions).HandleAsync(new AddBookmark(Id, Bookmark.ForFile("Carpeta/X.md")), Ct);

        await v.Session.Get<BookmarkList>().RenameAsync(P("Carpeta"), P("Movida"), Ct);

        var items = (await LoadAsync(v)).Items;
        items.Select(b => b.Path).Should().Equal(null, "Movida/X.md");
        items[0].Items!.Select(b => b.Path).Should().Equal("Movida/X.md", "Movida", "Otra/Y.md");
    }

    [Fact]
    public async Task Rename_WithNoMatches_DoesNotSave()
    {
        var store = Store(new BookmarksDocument([Bookmark.ForFile("A.md"), Bookmark.ForSearch("x")]));
        var list = new BookmarkList((await CreateAsync()).Session, store);

        var result = await list.RenameAsync(P("Nada"), P("Otra"), Ct);

        result.IsSuccess.Should().BeTrue();
        await store.DidNotReceive().SaveBookmarksAsync(Arg.Any<IVaultFileSystem>(), Arg.Any<BookmarksDocument>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rename_LeavesUnparsableBookmarkPathsAlone()
    {
        await using var v = await CreateAsync();

        await new AddBookmarkHandler(v.Sessions).HandleAsync(new AddBookmark(Id, new Bookmark(BookmarkKind.File, "bad?.md")), Ct);
        await new AddBookmarkHandler(v.Sessions).HandleAsync(new AddBookmark(Id, Bookmark.ForFile("A.md")), Ct);

        var result = await v.Session.Get<BookmarkList>().RenameAsync(P("A.md"), P("B.md"), Ct);

        result.IsSuccess.Should().BeTrue();
        (await LoadAsync(v)).Items.Select(b => b.Path).Should().Equal("bad?.md", "B.md");
    }

    [Fact]
    public async Task ProductivityInitializer_RejectsNullSessions()
    {
        var initializer = new ProductivitySessionInitializer(new InMemoryVaultConfigStore(), new InMemoryRecentFilesStore(), Substitute.For<Basalt.Application.Ports.Events.IEventBus>());

        await FluentActions.Awaiting(() => initializer.InitializeAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RenameEvents_UpdateBookmarksAndRecentsOnlyForTheirVault()
    {
        await using var v = await CreateAsync(("A.md", ""));
        await new AddBookmarkHandler(v.Sessions).HandleAsync(new AddBookmark(Id, Bookmark.ForFile("A.md")), Ct);
        await v.Session.Get<RecentFiles>().RecordAsync(P("A.md"), Ct);

        await v.Events.PublishAsync(new EntryRenamed(new VaultId(Guid.NewGuid()), P("A.md"), P("Ignored.md")), Ct);
        await v.Events.PublishAsync(new EntryRenamed(Id, P("A.md"), P("B.md")), Ct);
        await v.Events.WaitForIdleAsync(Ct);

        (await LoadAsync(v)).Items.Single().Path.Should().Be("B.md");
        (await v.Session.Get<RecentFiles>().LoadAsync(Ct)).Should().Equal(P("B.md"));
    }

    [Fact]
    public async Task DeleteEvents_RemoveRecentsOnlyForTheirVault()
    {
        await using var v = await CreateAsync(("A.md", ""));
        await v.Session.Get<RecentFiles>().RecordAsync(P("A.md"), Ct);

        await v.Events.PublishAsync(new EntriesDeleted(new VaultId(Guid.NewGuid()), [P("A.md")]), Ct);
        await v.Events.PublishAsync(new EntriesDeleted(Id, [P("A.md")]), Ct);
        await v.Events.WaitForIdleAsync(Ct);

        (await v.Session.Get<RecentFiles>().LoadAsync(Ct)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("list")]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task Handlers_RejectClosedVaults(string operation)
    {
        await using var v = await CreateAsync();
        var result = operation switch
        {
            "list" => (await new ListBookmarksHandler(v.Sessions).HandleAsync(new ListBookmarks(Closed), Ct)).Error,
            "add" => (await new AddBookmarkHandler(v.Sessions).HandleAsync(new AddBookmark(Closed, Bookmark.ForFile("A.md")), Ct)).Error,
            _ => (await new RemoveBookmarkHandler(v.Sessions).HandleAsync(new RemoveBookmark(Closed, Bookmark.ForFile("A.md")), Ct)).Error,
        };

        result.Code.Should().Be(ErrorCodes.VaultNotOpen);
    }

    [Fact]
    public async Task Handlers_RejectNullCommands()
    {
        await using var v = await CreateAsync();

        await FluentActions.Awaiting(() => new ListBookmarksHandler(v.Sessions).HandleAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => new AddBookmarkHandler(v.Sessions).HandleAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => new RemoveBookmarkHandler(v.Sessions).HandleAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AddBookmark_CreatesAndReusesGroups_AndIgnoresDuplicates()
    {
        await using var v = await CreateAsync();
        var add = new AddBookmarkHandler(v.Sessions);

        await add.HandleAsync(new AddBookmark(Id, Bookmark.ForFile("A.md")), Ct);
        await add.HandleAsync(new AddBookmark(Id, Bookmark.ForFile("B.md"), "G"), Ct);
        await add.HandleAsync(new AddBookmark(Id, Bookmark.ForFile("C.md"), "G"), Ct);
        await add.HandleAsync(new AddBookmark(Id, Bookmark.ForFile("C.md"), "G"), Ct);
        await add.HandleAsync(new AddBookmark(Id, Bookmark.ForFile("A.md")), Ct);

        var items = (await LoadAsync(v)).Items;
        items.Should().HaveCount(2);
        items[0].Should().Be(Bookmark.ForFile("A.md"));
        items[1].Items!.Select(b => b.Path).Should().Equal("B.md", "C.md");
    }

    [Fact]
    public async Task RemoveBookmark_SearchesInsideGroupsAndReportsMissingTargets()
    {
        await using var v = await CreateAsync();
        await new AddBookmarkHandler(v.Sessions).HandleAsync(new AddBookmark(Id, Bookmark.ForFile("A.md"), "G"), Ct);
        var remove = new RemoveBookmarkHandler(v.Sessions);

        var removed = await remove.HandleAsync(new RemoveBookmark(Id, Bookmark.ForFile("A.md")), Ct);
        var missing = await remove.HandleAsync(new RemoveBookmark(Id, Bookmark.ForFile("A.md")), Ct);

        removed.IsSuccess.Should().BeTrue();
        (await LoadAsync(v)).Items.Single().Items.Should().BeEmpty();
        missing.Error.Code.Should().Be(ErrorCodes.BookmarkNotFound);
        missing.Error.Message.Should().Be("There is no such bookmark.");
    }
}



