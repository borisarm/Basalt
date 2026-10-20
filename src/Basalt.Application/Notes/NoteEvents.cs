using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.FileSystem;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Notes;

/// <summary>A note was saved by Basalt.</summary>
public sealed record NoteSaved(VaultId Id, VaultPath Path) : IApplicationEvent
{
    VaultId? IApplicationEvent.VaultId => Id;
}

/// <summary>Files or folders were created by Basalt (notes, folders, attachments).</summary>
public sealed record EntriesCreated(VaultId Id, IReadOnlyList<VaultPath> Paths) : IApplicationEvent
{
    VaultId? IApplicationEvent.VaultId => Id;
}

/// <summary>A file or folder was renamed/moved by Basalt (a folder rename covers its content).</summary>
public sealed record EntryRenamed(VaultId Id, VaultPath From, VaultPath To) : IApplicationEvent
{
    VaultId? IApplicationEvent.VaultId => Id;
}

/// <summary>Files or folders were deleted (or moved to the trash) by Basalt.</summary>
public sealed record EntriesDeleted(VaultId Id, IReadOnlyList<VaultPath> Paths) : IApplicationEvent
{
    VaultId? IApplicationEvent.VaultId => Id;
}

/// <summary>Synchronisation wrote or deleted these files (spec 06 §6 step 8).</summary>
public sealed record FilesChangedBySync(VaultId Id, IReadOnlyList<VaultPath> Paths) : IApplicationEvent
{
    VaultId? IApplicationEvent.VaultId => Id;
}

/// <summary>Another application changed the vault (from the file watcher).</summary>
public sealed record VaultFileChangedExternally(VaultId Id, FileSystemChange Change) : IApplicationEvent
{
    VaultId? IApplicationEvent.VaultId => Id;
}

/// <summary>The metadata cache changed for these paths (panels refresh, spec 08 §3).</summary>
public sealed record MetadataChanged(VaultId Id, IReadOnlyCollection<VaultPath> Paths) : IApplicationEvent
{
    VaultId? IApplicationEvent.VaultId => Id;
}

/// <summary>Progress of the indexing at vault open ("Indexando… n/m").</summary>
public sealed record IndexingProgressChanged(VaultId Id, int Done, int Total) : IApplicationEvent
{
    VaultId? IApplicationEvent.VaultId => Id;
}
