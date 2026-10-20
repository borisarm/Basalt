using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Basalt.Application.Bookmarks;
using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Storage;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;

namespace Basalt.Adapters.Platform.Json;

/// <summary>Shared reading/writing of the indented JSON documents kept in a vault's <c>.basalt/</c> folder (spec 08 §1.1).</summary>
internal static class VaultJson
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n" };

    /// <summary>Reads the document as a JSON object without its <c>schemaVersion</c>; null when missing.</summary>
    public static async Task<(JsonObject? Root, DocumentLoadStatus Status, string? Problem)> ReadAsync(IVaultFileSystem vault, VaultPath path, int schemaVersion, CancellationToken ct)
    {
        var text = await vault.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        if (text.IsFailure)
        {
            return text.Error.Code == ErrorCodes.FsNotFound ? (null, DocumentLoadStatus.Missing, null) : (null, DocumentLoadStatus.Invalid, text.Error.Message);
        }

        try
        {
            if (JsonNode.Parse(text.Value) is not JsonObject root)
            {
                return (null, DocumentLoadStatus.Invalid, "The document is not a JSON object.");
            }

            if ((root["schemaVersion"]?.GetValue<int>() ?? 1) > schemaVersion)
            {
                return (null, DocumentLoadStatus.Invalid, "The document was written by a newer version of Basalt.");
            }

            root.Remove("schemaVersion");
            return (root, DocumentLoadStatus.Loaded, null);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return (null, DocumentLoadStatus.Invalid, e.Message);
        }
    }

    /// <summary>Writes <c>{ "schemaVersion": n, ...content }</c>, indented, with a trailing newline.</summary>
    public static Task<Result<Unit>> WriteAsync(IVaultFileSystem vault, VaultPath path, int schemaVersion, JsonObject content, CancellationToken ct)
    {
        var document = new JsonObject { ["schemaVersion"] = schemaVersion };
        foreach (var (key, value) in content.ToList())
        {
            content.Remove(key);
            document[key] = value;
        }

        return vault.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(document.ToJsonString(Indented) + "\n"), null, ct);
    }

    /// <summary>Moves an unreadable document to <c>{name}.invalid.json</c> (" 2", " 3"… if taken) so that it is never lost.</summary>
    public static async Task<Result<Unit>> KeepAsideAsync(IVaultFileSystem vault, VaultPath path, CancellationToken ct)
    {
        for (var n = 1; ; n++)
        {
            var name = path.NameWithoutExtension + ".invalid" + (n == 1 ? string.Empty : " " + n.ToString(CultureInfo.InvariantCulture)) + ".json";
            var target = path.WithName(name).Value;
            if (!await vault.ExistsAsync(target, ct).ConfigureAwait(false))
            {
                return await vault.MoveAsync(path, target, ct).ConfigureAwait(false);
            }
        }
    }
}

/// <summary><c>.basalt/bookmarks.json</c> in an Obsidian-like shape: <c>{ "items": [ { "type": "file", "path": … } ] }</c>.</summary>
internal static class BookmarksJson
{
    public const int SchemaVersion = 1;
    public static readonly VaultPath Path = VaultPath.Create(".basalt/bookmarks.json").Value;

    public static JsonObject Write(BookmarksDocument document) => new() { ["items"] = Items(document.Items) };

    /// <returns>The bookmarks, or the problem that makes the document unusable (unknown types are kept by refusing it).</returns>
    public static (BookmarksDocument? Document, string? Problem) Read(JsonObject root)
    {
        try
        {
            var items = new List<Bookmark>();
            foreach (var node in root["items"]?.AsArray() ?? [])
            {
                var (bookmark, problem) = ReadItem(node);
                if (bookmark is null)
                {
                    return (null, problem);
                }

                items.Add(bookmark);
            }

            return (new BookmarksDocument(items), null);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return (null, e.Message);
        }
    }

    private static JsonArray Items(IEnumerable<Bookmark> items) => [.. items.Select(Item)];

    private static JsonObject Item(Bookmark bookmark)
    {
        var node = new JsonObject { ["type"] = bookmark.Kind.ToString().ToLowerInvariant() };
        Add(node, "path", bookmark.Path);
        Add(node, "subpath", bookmark.Subpath);
        Add(node, "query", bookmark.Query);
        Add(node, "title", bookmark.Title);
        if (bookmark.Kind == BookmarkKind.Group)
        {
            node["items"] = Items(bookmark.Items ?? []);
        }

        return node;
    }

    private static void Add(JsonObject node, string key, string? value)
    {
        if (value is not null)
        {
            node[key] = value;
        }
    }

    private static (Bookmark? Bookmark, string? Problem) ReadItem(JsonNode? node)
    {
        if (node is not JsonObject item)
        {
            return (null, "A bookmark is not an object.");
        }

        var type = item["type"]?.GetValue<string>();
        BookmarkKind? kind = type switch
        {
            "file" => BookmarkKind.File,
            "folder" => BookmarkKind.Folder,
            "search" => BookmarkKind.Search,
            "group" => BookmarkKind.Group,
            _ => null,
        };
        if (kind is null)
        {
            return (null, $"Unknown bookmark type '{type}'.");
        }

        List<Bookmark>? children = null;
        if (kind == BookmarkKind.Group)
        {
            children = [];
            foreach (var child in item["items"]?.AsArray() ?? [])
            {
                var (bookmark, problem) = ReadItem(child);
                if (bookmark is null)
                {
                    return (null, problem);
                }

                children.Add(bookmark);
            }
        }

        return (new Bookmark(kind.Value, Text(item, "path"), Text(item, "subpath"), Text(item, "query"), Text(item, "title"), children), null);
    }

    private static string? Text(JsonObject item, string key) => item[key]?.GetValue<string>();
}

/// <summary><c>{AppData}/vaults/{vaultId}/recent.json</c>.</summary>
internal sealed class JsonRecentFilesStore(IVaultLocalDataStore local, TimeProvider time) : IRecentFilesStore
{
    public async Task<IReadOnlyList<VaultPath>> LoadAsync(Domain.Vaults.VaultId vault, CancellationToken ct)
    {
        var load = await Store(vault).LoadAsync(ct).ConfigureAwait(false);
        return [.. load.Value.Files.Select(VaultPath.Create).Where(p => p.IsSuccess).Select(p => p.Value)];
    }

    public Task<Result<Unit>> SaveAsync(Domain.Vaults.VaultId vault, IReadOnlyList<VaultPath> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Store(vault).SaveAsync(new RecentFilesDocument([.. files.Select(f => f.ToString())]), ct);
    }

    private JsonFileStore<RecentFilesDocument> Store(Domain.Vaults.VaultId vault) => new(
        System.IO.Path.Combine(local.DirectoryOf(vault), "recent.json"),
        PlatformJsonContext.Default.RecentFilesDocument,
        () => RecentFilesDocument.Empty,
        currentVersion: 1,
        migrations: [],
        indented: false,
        time);
}

/// <summary>JSON shape of <c>recent.json</c>.</summary>
internal sealed record RecentFilesDocument(IReadOnlyList<string> Files)
{
    public static RecentFilesDocument Empty { get; } = new([]);
}

/// <summary><c>.basalt/workspace.{deviceId}.json</c>.</summary>
internal static class WorkspaceJson
{
    public const int SchemaVersion = 1;

    public static VaultPath PathFor(Guid deviceId) => VaultPath.Create($".basalt/workspace.{deviceId:D}.json").Value;
}
