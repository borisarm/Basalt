namespace Basalt.Application.Bookmarks;

public enum BookmarkKind
{
    /// <summary>A note or attachment; with <see cref="Bookmark.Subpath"/> (<c>#Heading</c>, <c>#^block</c>) a heading or block.</summary>
    File,
    Folder,
    Search,
    Group,
}

/// <summary>
/// An entry of <c>.basalt/bookmarks.json</c> (F-011), shaped like Obsidian's bookmarks: files (optionally with a
/// subpath), folders, saved searches and titled groups of entries.
/// </summary>
public sealed record Bookmark(
    BookmarkKind Kind,
    string? Path = null,
    string? Subpath = null,
    string? Query = null,
    string? Title = null,
    IReadOnlyList<Bookmark>? Items = null)
{
    public static Bookmark ForFile(string path, string? subpath = null, string? title = null) => new(BookmarkKind.File, path, subpath, Title: title);

    public static Bookmark ForFolder(string path) => new(BookmarkKind.Folder, path);

    public static Bookmark ForSearch(string query, string? title = null) => new(BookmarkKind.Search, Query: query, Title: title);

    public static Bookmark ForGroup(string title, params Bookmark[] items) => new(BookmarkKind.Group, Title: title, Items: items);

    /// <summary>Same target (groups compare by title); the contents of groups are ignored.</summary>
    public bool SameTargetAs(Bookmark other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Kind == other.Kind
            && string.Equals(Path, other.Path, StringComparison.Ordinal)
            && string.Equals(Subpath, other.Subpath, StringComparison.Ordinal)
            && string.Equals(Query, other.Query, StringComparison.Ordinal)
            && (Kind != BookmarkKind.Group || string.Equals(Title, other.Title, StringComparison.Ordinal));
    }
}

/// <summary>The bookmarks of a vault (synchronised).</summary>
public sealed record BookmarksDocument(IReadOnlyList<Bookmark> Items)
{
    public static BookmarksDocument Empty { get; } = new([]);
}
