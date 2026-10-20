using Basalt.Application.Abstractions;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Notes;

public enum FileTreeSort
{
    NameAscending,
    NameDescending,
    ModifiedNewest,
    ModifiedOldest,
}

/// <summary>A node of the explorer tree. Notes are displayed without ".md" by the UI.</summary>
public sealed record FileTreeNode(VaultPath Path, bool IsFolder, FileKind Kind, DateTimeOffset LastWriteUtc, IReadOnlyList<FileTreeNode> Children);

/// <summary>The explorer tree (F-002): hidden entries (".basalt", ".trash", dot-files) and temporary files are excluded;
/// folders first, then the chosen order.</summary>
public sealed record GetFileTree(VaultId VaultId, FileTreeSort Sort = FileTreeSort.NameAscending) : IQuery<FileTreeNode>, IVaultRequest;

public sealed class GetFileTreeHandler(IVaultSessions sessions) : IQueryHandler<GetFileTree, FileTreeNode>
{
    public async Task<Result<FileTreeNode>> HandleAsync(GetFileTree query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var entries = new List<FileEntry>();
        await foreach (var entry in session.Value.FileSystem.EnumerateAsync(VaultPath.Root, recursive: true, ct).ConfigureAwait(false))
        {
            if (!entry.Path.IsHidden)
            {
                entries.Add(entry);
            }
        }

        var byParent = entries.ToLookup(e => e.Path.Parent);
        return Build(new FileEntry(VaultPath.Root, true, 0, DateTimeOffset.MinValue), byParent, query.Sort);
    }

    private static FileTreeNode Build(FileEntry entry, ILookup<VaultPath, FileEntry> byParent, FileTreeSort sort)
    {
        var children = entry.IsDirectory
            ? Order(byParent[entry.Path], sort).Select(child => Build(child, byParent, sort)).ToList()
            : [];
        return new FileTreeNode(entry.Path, entry.IsDirectory, entry.IsDirectory ? FileKind.Other : FileClassifier.Classify(entry.Path), entry.LastWriteUtc, children);
    }

    private static IEnumerable<FileEntry> Order(IEnumerable<FileEntry> entries, FileTreeSort sort)
    {
        var foldersFirst = entries.OrderByDescending(e => e.IsDirectory);
        var byName = StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering);
        return sort switch
        {
            FileTreeSort.NameDescending => foldersFirst.ThenByDescending(e => e.Path.Name, byName),
            FileTreeSort.ModifiedNewest => foldersFirst.ThenByDescending(e => e.LastWriteUtc).ThenBy(e => e.Path.Name, byName),
            FileTreeSort.ModifiedOldest => foldersFirst.ThenBy(e => e.LastWriteUtc).ThenBy(e => e.Path.Name, byName),
            _ => foldersFirst.ThenBy(e => e.Path.Name, byName),
        };
    }
}
