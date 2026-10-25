using System.Collections.ObjectModel;
using Basalt.Application.Abstractions;
using Basalt.Application.Bookmarks;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Index;
using Basalt.Application.Search;
using Basalt.Application.Tags;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Tags;
using Basalt.Presentation.Common;
using Basalt.Presentation.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Panels;

public sealed record FileExplorerUseCases(
    IQueryHandler<GetFileTree, FileTreeNode> Tree,
    ICommandHandler<CreateNote, VaultPath> CreateNote,
    ICommandHandler<CreateFolder, VaultPath> CreateFolder,
    ICommandHandler<RenameEntry, VaultPath> Rename,
    ICommandHandler<DeleteEntries, Unit> Delete);

/// <summary>A node of the explorer; children are created when first needed (large vaults).</summary>
public sealed partial class FileTreeNodeViewModel(FileTreeNode node) : ObservableObject
{
    private List<FileTreeNodeViewModel>? _children;

    public VaultPath Path { get; } = node.Path;

    public bool IsFolder { get; } = node.IsFolder;

    /// <summary>A Markdown note, as opposed to a folder or an attachment (the explorer gives each its own icon).</summary>
    public bool IsNote { get; } = !node.IsFolder && node.Kind == FileKind.Note;

    /// <summary>Notes are shown without <c>.md</c>.</summary>
    public string Name { get; } = node.Kind == FileKind.Note ? node.Path.NameWithoutExtension : node.Path.Name;

    public IReadOnlyList<FileTreeNodeViewModel> Children => _children ??= [.. node.Children.Select(c => new FileTreeNodeViewModel(c))];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

/// <summary>The file explorer (F-002): tree, open, create notes and folders, rename, delete to the vault trash.</summary>
public sealed partial class FileExplorerViewModel : ViewModelBase
{
    private readonly PanelContext _context;
    private readonly FileExplorerUseCases _useCases;
    private readonly IDialogService _dialogs;
    private readonly IDisposable[] _subscriptions;

    public FileExplorerViewModel(ViewModelServices services, PanelContext context, FileExplorerUseCases useCases, IDialogService dialogs)
        : base(services)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _useCases = useCases;
        _dialogs = dialogs;
        _subscriptions =
        [
            Refresh<EntriesCreated>(e => e.Id),
            Refresh<EntryRenamed>(e => e.Id),
            Refresh<EntriesDeleted>(e => e.Id),
            Refresh<FilesChangedBySync>(e => e.Id),
            Refresh<VaultFileChangedExternally>(e => e.Id),
        ];
        context.Messenger.Register<RevealInExplorerRequested>(this, (_, m) => Reveal(m.Path));
    }

    public ObservableCollection<FileTreeNodeViewModel> Roots { get; } = [];

    [ObservableProperty]
    public partial FileTreeNodeViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial FileTreeSort Sort { get; set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _context.Messenger.UnregisterAll(this);
        }

        base.Dispose(disposing);
    }

    partial void OnSortChanged(FileTreeSort value) => _ = RefreshAsync();

    private IDisposable Refresh<TEvent>(Func<TEvent, Domain.Vaults.VaultId> vault)
        where TEvent : class, Application.Ports.Events.IApplicationEvent =>
        _context.Events.Subscribe<TEvent>(async (e, _) =>
        {
            if (vault(e) == _context.Vault)
            {
                await Services.Dispatcher.InvokeAsync(RefreshAsync).ConfigureAwait(false);
            }
        });

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var tree = await RunAsync(ct => _useCases.Tree.HandleAsync(new GetFileTree(_context.Vault, Sort), ct)).ConfigureAwait(true);
        if (tree.IsFailure)
        {
            return;
        }

        var expanded = Expanded(Roots).Select(n => n.Path).ToHashSet();
        Roots.Clear();
        foreach (var child in tree.Value.Children)
        {
            Roots.Add(new FileTreeNodeViewModel(child));
        }

        foreach (var node in Flatten(Roots, n => expanded.Contains(n.Path)))
        {
            node.IsExpanded = true;
        }
    }

    [RelayCommand]
    private async Task OpenAsync(FileTreeNodeViewModel? node, CancellationToken ct)
    {
        if (node is null)
        {
            return;
        }

        if (node.IsFolder)
        {
            node.IsExpanded = !node.IsExpanded;
            return;
        }

        await _context.Navigator.OpenAsync(new OpenRequest(node.Path), ct).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task NewNoteAsync(FileTreeNodeViewModel? node, CancellationToken ct)
    {
        var created = await RunAsync(token => _useCases.CreateNote.HandleAsync(new CreateNote(_context.Vault, FolderOf(node)), token)).ConfigureAwait(true);
        if (created.IsSuccess)
        {
            await _context.Navigator.OpenAsync(new OpenRequest(created.Value), ct).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task NewFolderAsync(FileTreeNodeViewModel? node, CancellationToken ct)
    {
        var name = await _dialogs.PromptAsync(new PromptRequest(Localizer.Get("Explorer.NewFolder"), Localizer.Get("Explorer.FolderName")), ct).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || FolderOf(node).Combine(name.Trim()) is not { IsSuccess: true } path)
        {
            return;
        }

        await RunAsync(token => _useCases.CreateFolder.HandleAsync(new CreateFolder(_context.Vault, path.Value), token)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RenameAsync(FileTreeNodeViewModel? node, CancellationToken ct)
    {
        if (node is null)
        {
            return;
        }

        var name = await _dialogs.PromptAsync(new PromptRequest(Localizer.Get("Explorer.Rename"), Localizer.Get("Explorer.NewName"), node.Name), ct).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(name) && name != node.Name)
        {
            await RunAsync(token => _useCases.Rename.HandleAsync(new RenameEntry(_context.Vault, node.Path, name.Trim()), token)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(FileTreeNodeViewModel? node, CancellationToken ct)
    {
        if (node is null)
        {
            return;
        }

        var delete = Localizer.Get("Explorer.Delete");
        if (await _dialogs.ConfirmAsync(new ConfirmRequest(delete, Localizer.Format("Explorer.DeleteMessage", node.Name), delete, IsDestructive: true), ct).ConfigureAwait(true))
        {
            await RunAsync(token => _useCases.Delete.HandleAsync(new DeleteEntries(_context.Vault, [node.Path]), token)).ConfigureAwait(true);
        }
    }

    private static VaultPath FolderOf(FileTreeNodeViewModel? node) => node is null ? VaultPath.Root : node.IsFolder ? node.Path : node.Path.Parent;

    /// <summary>Expands the folders on the way to <paramref name="path"/> (only those) and selects it.</summary>
    private void Reveal(VaultPath path)
    {
        IReadOnlyList<FileTreeNodeViewModel> level = Roots;
        while (level.FirstOrDefault(n => n.Path == path || path.IsDescendantOf(n.Path)) is { } next)
        {
            if (next.Path == path)
            {
                Selected = next;
                return;
            }

            next.IsExpanded = true;
            level = next.Children;
        }
    }

    private static IEnumerable<FileTreeNodeViewModel> Expanded(IEnumerable<FileTreeNodeViewModel> nodes) =>
        nodes.Where(n => n.IsExpanded).SelectMany(n => Expanded(n.Children).Prepend(n));

    private static IEnumerable<FileTreeNodeViewModel> Flatten(IEnumerable<FileTreeNodeViewModel> nodes, Func<FileTreeNodeViewModel, bool> descend) =>
        nodes.SelectMany(n => descend(n) && n.IsFolder ? Flatten(n.Children, descend).Prepend(n) : [n]);
}

/// <summary>A file with matches in the search panel.</summary>
public sealed class SearchResultGroupViewModel(SearchHit hit)
{
    public VaultPath Path { get; } = hit.Path;

    public string Title { get; } = hit.Kind == FileKind.Note ? hit.Path.NameWithoutExtension : hit.Path.Name;

    public int MatchCount { get; } = hit.Ranges.Count;

    public SearchHit Hit { get; } = hit;
}

/// <summary>
/// The search panel (F-007): searches 250 ms after the last keystroke, cancelling the previous search; syntax errors
/// show inline without blocking (F-007.4).
/// </summary>
public sealed partial class SearchPanelViewModel : ViewModelBase
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly PanelContext _context;
    private readonly IQueryHandler<SearchNotes, IReadOnlyList<SearchHit>> _search;
    private ITimer? _debounce;
    private CancellationTokenSource? _running;
    private Task _searching = Task.CompletedTask;

    public SearchPanelViewModel(ViewModelServices services, PanelContext context, IQueryHandler<SearchNotes, IReadOnlyList<SearchHit>> search)
        : base(services)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _search = search;
        context.Messenger.Register<SearchRequested>(this, (_, m) =>
        {
            Query = m.Query;
            _debounce?.Dispose();
            _searching = SearchNowAsync();
            _context.Navigator.FocusPanel(PanelIds.Search);
        });
    }

    public ObservableCollection<SearchResultGroupViewModel> Results { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    [ObservableProperty]
    public partial SearchSort Sort { get; set; }

    /// <summary>The inline message for a query that cannot run.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Completes when the search in progress (if any) has finished.</summary>
    public Task WhenSearchedAsync() => _searching;

    partial void OnQueryChanged(string value) => Schedule();

    partial void OnMatchCaseChanged(bool value) => Schedule();

    partial void OnSortChanged(SearchSort value) => Schedule();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _debounce?.Dispose();
            _running?.Cancel();
            _running?.Dispose();
            _context.Messenger.UnregisterAll(this);
        }

        base.Dispose(disposing);
    }

    private void Schedule()
    {
        _debounce?.Dispose();
        _debounce = Services.Time.CreateTimer(_ => Services.Dispatcher.Post(() => _searching = SearchNowAsync()), null, Debounce, Timeout.InfiniteTimeSpan);
    }

    private async Task SearchNowAsync()
    {
        _running?.Cancel();
        _running?.Dispose();
        var running = _running = new CancellationTokenSource();
        if (string.IsNullOrWhiteSpace(Query))
        {
            Results.Clear();
            Error = null;
            return;
        }

        var options = new SearchOptions(Sort, MatchCase);
        var query = Query;
        var hits = await RunAsync(
            async ct =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, running.Token);
                return await _search.HandleAsync(new SearchNotes(_context.Vault, query, options), linked.Token).ConfigureAwait(true);
            },
            notifyErrors: false).ConfigureAwait(true);
        if (running.IsCancellationRequested)
        {
            return;
        }

        Results.Clear();
        if (hits.IsFailure)
        {
            Error = Localizer.Error(hits.Error);
            return;
        }

        Error = null;
        foreach (var hit in hits.Value)
        {
            Results.Add(new SearchResultGroupViewModel(hit));
        }
    }

    [RelayCommand]
    private Task OpenAsync(SearchResultGroupViewModel? result, CancellationToken ct) =>
        result is null ? Task.CompletedTask : _context.Navigator.OpenAsync(new OpenRequest(result.Path), ct);
}

/// <summary>A bookmark entry.</summary>
public sealed partial class BookmarkItemViewModel(Bookmark bookmark) : ObservableObject
{
    public Bookmark Bookmark { get; } = bookmark;

    public string Title { get; } = TitleOf(bookmark);

    public IReadOnlyList<BookmarkItemViewModel> Children { get; } = [.. (bookmark.Items ?? []).Select(i => new BookmarkItemViewModel(i))];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    private static string TitleOf(Bookmark bookmark)
    {
        if (bookmark.Title is { } title)
        {
            return title;
        }

        var path = bookmark.Path is { } text && VaultPath.Create(text) is { IsSuccess: true } parsed ? parsed.Value : (VaultPath?)null;
        return bookmark.Kind switch
        {
            BookmarkKind.File when path is { } file => file.NameWithoutExtension + (bookmark.Subpath is { } sub ? " › " + sub.TrimStart('#', '^') : string.Empty),
            BookmarkKind.Folder when path is { } folder => folder.Name,
            BookmarkKind.Search => bookmark.Query ?? string.Empty,
            _ => bookmark.Path ?? string.Empty,
        };
    }
}

/// <summary>The bookmarks panel (F-011).</summary>
public sealed partial class BookmarksViewModel : ViewModelBase
{
    private readonly PanelContext _context;
    private readonly IQueryHandler<ListBookmarks, BookmarksView> _list;
    private readonly ICommandHandler<RemoveBookmark, Unit> _remove;
    private readonly IDisposable _renames;

    public BookmarksViewModel(ViewModelServices services, PanelContext context, IQueryHandler<ListBookmarks, BookmarksView> list, ICommandHandler<RemoveBookmark, Unit> remove)
        : base(services)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _list = list;
        _remove = remove;
        _renames = context.Events.Subscribe<EntryRenamed>(async (e, _) =>
        {
            if (e.Id == context.Vault)
            {
                await Services.Dispatcher.InvokeAsync(RefreshAsync).ConfigureAwait(false);
            }
        });
    }

    public ObservableCollection<BookmarkItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string? Warning { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renames.Dispose();
        }

        base.Dispose(disposing);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var view = await RunAsync(ct => _list.HandleAsync(new ListBookmarks(_context.Vault), ct)).ConfigureAwait(true);
        if (view.IsFailure)
        {
            return;
        }

        Items.Clear();
        foreach (var item in view.Value.Document.Items)
        {
            Items.Add(new BookmarkItemViewModel(item));
        }

        Warning = view.Value.IsInvalid ? Localizer.Get("Bookmarks.Invalid") : null;
    }

    [RelayCommand]
    private async Task OpenAsync(BookmarkItemViewModel? item, CancellationToken ct)
    {
        var bookmark = item?.Bookmark;
        var path = bookmark?.Path is { } text && VaultPath.Create(text) is { IsSuccess: true } parsed ? parsed.Value : (VaultPath?)null;
        switch (bookmark?.Kind)
        {
            case BookmarkKind.File when path is { } file:
                await _context.Navigator.OpenAsync(new OpenRequest(file, Subpath: bookmark.Subpath), ct).ConfigureAwait(true);
                break;
            case BookmarkKind.Folder when path is { } folder:
                _context.Messenger.Send(new RevealInExplorerRequested(folder));
                break;
            case BookmarkKind.Search when bookmark.Query is { } query:
                _context.Messenger.Send(new SearchRequested(query));
                break;
            case BookmarkKind.Group:
                item!.IsExpanded = !item.IsExpanded;
                break;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(BookmarkItemViewModel? item)
    {
        if (item is not null && (await RunAsync(ct => _remove.HandleAsync(new RemoveBookmark(_context.Vault, item.Bookmark), ct)).ConfigureAwait(true)).IsSuccess)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
    }
}

/// <summary>A tag of the tags panel.</summary>
public sealed class TagNodeViewModel(TagNode node)
{
    public string Name { get; } = node.Segment;

    public string FullName { get; } = node.FullName;

    public int Count { get; } = node.Count;

    public IReadOnlyList<TagNodeViewModel> Children { get; } = [.. node.Children.Select(c => new TagNodeViewModel(c))];
}

/// <summary>The tags panel (F-006): tree with counters; choosing a tag searches <c>tag:#x</c>.</summary>
public sealed partial class TagsPanelViewModel : ViewModelBase
{
    private readonly PanelContext _context;
    private readonly IQueryHandler<GetTags, TagHierarchy> _tags;
    private readonly IDisposable _metadata;

    public TagsPanelViewModel(ViewModelServices services, PanelContext context, IQueryHandler<GetTags, TagHierarchy> tags)
        : base(services)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _tags = tags;
        _metadata = context.Events.Subscribe<MetadataChanged>(async (e, _) =>
        {
            if (e.Id == context.Vault)
            {
                await Services.Dispatcher.InvokeAsync(RefreshAsync).ConfigureAwait(false);
            }
        });
    }

    public ObservableCollection<TagNodeViewModel> Roots { get; } = [];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _metadata.Dispose();
        }

        base.Dispose(disposing);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var tags = await RunAsync(ct => _tags.HandleAsync(new GetTags(_context.Vault), ct)).ConfigureAwait(true);
        if (tags.IsSuccess)
        {
            Roots.Clear();
            foreach (var root in tags.Value.Roots)
            {
                Roots.Add(new TagNodeViewModel(root));
            }
        }
    }

    [RelayCommand]
    private void SearchTag(TagNodeViewModel? tag)
    {
        if (tag is not null)
        {
            _context.Messenger.Send(new SearchRequested("tag:#" + tag.FullName));
        }
    }
}
