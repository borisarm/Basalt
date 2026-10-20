using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Vaults;
using Basalt.Application.Workspace;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Bookmarks;

/// <summary>
/// The bookmarks of an open vault: serialised read-modify-write of <c>.basalt/bookmarks.json</c>. Paths follow
/// renames and moves (F-011.1); bookmarks of deleted files are kept (they may come back, e.g. from the trash or a sync).
/// </summary>
public sealed class BookmarkList(VaultSession session, IVaultConfigStore store) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IVaultConfigStore Store => store;

    public Task<DocumentLoad<BookmarksDocument>> LoadAsync(CancellationToken ct) => store.LoadBookmarksAsync(session.FileSystem, ct);

    /// <summary>Applies <paramref name="change"/>; it returns null (nothing to save) or an error to abort.</summary>
    public async Task<Result<Unit>> UpdateAsync(Func<BookmarksDocument, Result<BookmarksDocument?>> change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await LoadAsync(ct).ConfigureAwait(false);
            var changed = change(current.Value);
            if (changed.IsFailure)
            {
                return changed.Error;
            }

            return changed.Value is { } document ? await store.SaveBookmarksAsync(session.FileSystem, document, ct).ConfigureAwait(false) : Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<Result<Unit>> RenameAsync(VaultPath from, VaultPath to, CancellationToken ct) =>
        UpdateAsync(document =>
        {
            var renamed = Rename(document.Items, from, to, out var changed);
            return changed ? new BookmarksDocument(renamed) : Result.Success<BookmarksDocument?>(null);
        }, ct);

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private static List<Bookmark> Rename(IEnumerable<Bookmark> items, VaultPath from, VaultPath to, out bool changed)
    {
        changed = false;
        var result = new List<Bookmark>();
        foreach (var item in items)
        {
            var updated = item;
            if (item.Path is { } path && VaultPath.Create(path) is { IsSuccess: true } parsed && (parsed.Value == from || parsed.Value.IsDescendantOf(from)))
            {
                var moved = parsed.Value == from ? to : to.Combine(parsed.Value.RelativeTo(from)).Value;
                updated = updated with { Path = moved.ToString() };
                changed = true;
            }

            if (item.Items is { } children)
            {
                updated = updated with { Items = Rename(children, from, to, out var childChanged) };
                changed |= childChanged;
            }

            result.Add(updated);
        }

        return result;
    }
}

/// <summary>Creates the per-vault bookmark and recent-file components and keeps them in sync with file operations.</summary>
public sealed class ProductivitySessionInitializer(IVaultConfigStore configs, IRecentFilesStore recents, IEventBus events) : IVaultSessionInitializer
{
    public Task<Result<Unit>> InitializeAsync(VaultSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var bookmarks = new BookmarkList(session, configs);
        var recent = new RecentFiles(session.Id, recents);
        session.Own(bookmarks);
        session.Own(recent);
        session.Set(bookmarks);
        session.Set(recent);
        session.Own(new Subscriptions(
        [
            events.Subscribe<EntryRenamed>(async (e, c) =>
            {
                if (e.Id == session.Id)
                {
                    await bookmarks.RenameAsync(e.From, e.To, c).ConfigureAwait(false);
                    await recent.RenameAsync(e.From, e.To, c).ConfigureAwait(false);
                }
            }),
            events.Subscribe<EntriesDeleted>(async (e, c) =>
            {
                if (e.Id == session.Id)
                {
                    await recent.RemoveAsync(e.Paths, c).ConfigureAwait(false);
                }
            }),
        ]));
        return Task.FromResult(Result.Success());
    }

    private sealed class Subscriptions(IReadOnlyList<IDisposable> subscriptions) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The bookmarks and whether the stored document could be read (an invalid one is shown as empty with a warning).</summary>
public sealed record BookmarksView(BookmarksDocument Document, bool IsInvalid, string? Problem);

public sealed record ListBookmarks(VaultId VaultId) : IQuery<BookmarksView>, IVaultRequest;

public sealed class ListBookmarksHandler(IVaultSessions sessions) : IQueryHandler<ListBookmarks, BookmarksView>
{
    public async Task<Result<BookmarksView>> HandleAsync(ListBookmarks query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var load = await session.Value.Get<BookmarkList>().LoadAsync(ct).ConfigureAwait(false);
        return new BookmarksView(load.Value, load.Status == DocumentLoadStatus.Invalid, load.Problem);
    }
}

/// <summary>Adds a bookmark at the end of the root or of the group titled <paramref name="Group"/> (created if needed). Duplicates are ignored.</summary>
public sealed record AddBookmark(VaultId VaultId, Bookmark Item, string? Group = null) : ICommand<Unit>, IVaultRequest;

public sealed class AddBookmarkHandler(IVaultSessions sessions) : ICommandHandler<AddBookmark, Unit>
{
    public async Task<Result<Unit>> HandleAsync(AddBookmark command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = sessions.Get(command.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        return await session.Value.Get<BookmarkList>().UpdateAsync(document =>
        {
            if (command.Group is null)
            {
                return document.Items.Any(i => i.SameTargetAs(command.Item)) ? Result.Success<BookmarksDocument?>(null) : new BookmarksDocument([.. document.Items, command.Item]);
            }

            var items = document.Items.ToList();
            var index = items.FindIndex(i => i.Kind == BookmarkKind.Group && i.Title == command.Group);
            var group = index >= 0 ? items[index] : Bookmark.ForGroup(command.Group);
            if (group.Items?.Any(i => i.SameTargetAs(command.Item)) == true)
            {
                return Result.Success<BookmarksDocument?>(null);
            }

            group = group with { Items = [.. group.Items ?? [], command.Item] };
            if (index >= 0)
            {
                items[index] = group;
            }
            else
            {
                items.Add(group);
            }

            return new BookmarksDocument(items);
        }, ct).ConfigureAwait(false);
    }
}

/// <summary>Removes the first bookmark (searching groups too) with the same target.</summary>
/// <returns><c>bookmark.not_found</c>.</returns>
public sealed record RemoveBookmark(VaultId VaultId, Bookmark Item) : ICommand<Unit>, IVaultRequest;

public sealed class RemoveBookmarkHandler(IVaultSessions sessions) : ICommandHandler<RemoveBookmark, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RemoveBookmark command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = sessions.Get(command.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        return await session.Value.Get<BookmarkList>().UpdateAsync(document =>
        {
            var removed = false;
            var items = Remove(document.Items, command.Item, ref removed);
            return removed
                ? new BookmarksDocument(items)
                : new Error(ErrorCodes.BookmarkNotFound, "There is no such bookmark.");
        }, ct).ConfigureAwait(false);
    }

    private static List<Bookmark> Remove(IEnumerable<Bookmark> items, Bookmark target, ref bool removed)
    {
        var result = new List<Bookmark>();
        foreach (var item in items)
        {
            if (!removed && item.SameTargetAs(target))
            {
                removed = true;
                continue;
            }

            result.Add(item.Items is { } children && !removed ? item with { Items = Remove(children, target, ref removed) } : item);
        }

        return result;
    }
}
