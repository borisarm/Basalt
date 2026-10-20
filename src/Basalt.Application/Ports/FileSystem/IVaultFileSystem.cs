using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Application.Ports.FileSystem;

/// <summary>Metadata of a file or folder.</summary>
public sealed record FileStat(long Size, DateTimeOffset LastWriteUtc, bool IsDirectory);

/// <summary>An enumerated vault entry; symbolic links are reported but never traversed.</summary>
public sealed record FileEntry(VaultPath Path, bool IsDirectory, long Size, DateTimeOffset LastWriteUtc, bool IsSymbolicLink = false);

public enum FileSystemChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed,

    /// <summary>Events were lost (buffer overflow); consumers must rescan.</summary>
    RescanRequired,
}

/// <summary>A change observed by <see cref="IFileWatcher"/>; <see cref="OldPath"/> is set for renames.</summary>
public sealed record FileSystemChange(FileSystemChangeKind Kind, VaultPath Path, VaultPath? OldPath = null);

/// <summary>Opens the file system rooted at a vault location (spec 03 §1.1).</summary>
public interface IVaultFileSystemFactory
{
    /// <returns><c>fs.not_found</c> when the location does not exist.</returns>
    Task<Result<IVaultFileSystem>> OpenAsync(string rootLocation, CancellationToken ct);

    /// <summary>Creates the folder (and parents) and opens it.</summary>
    /// <returns><c>fs.already_exists</c> when the location exists and is not an empty folder.</returns>
    Task<Result<IVaultFileSystem>> CreateAsync(string rootLocation, CancellationToken ct);

    /// <summary>Platform-specific location of a child folder.</summary>
    string Combine(string parentLocation, string name);

    /// <summary>The folder name of a location (default vault name when opening a folder).</summary>
    string NameOf(string location);
}

/// <summary>Device-local data of a vault outside the vault folder (<c>{AppData}/vaults/{id}/</c>: index, sync state).</summary>
public interface IVaultLocalDataStore
{
    /// <summary>Deletes all local data of the vault; never touches the vault folder.</summary>
    Task<Result<Unit>> DeleteAsync(Domain.Vaults.VaultId vault, CancellationToken ct);

    /// <summary>Folder for the vault's local data (created on demand).</summary>
    string DirectoryOf(Domain.Vaults.VaultId vault);
}

/// <summary>Read access to vault files. Paths are relative to the vault root.</summary>
public interface IFileReader
{
    /// <returns><c>fs.not_found</c>, <c>fs.is_directory</c>, <c>fs.access_denied</c> or <c>fs.io</c>.</returns>
    Task<Result<byte[]>> ReadAllBytesAsync(VaultPath path, CancellationToken ct);

    /// <summary>Reads UTF-8 text, dropping a BOM if present.</summary>
    Task<Result<string>> ReadAllTextAsync(VaultPath path, CancellationToken ct);

    Task<Result<Stream>> OpenReadAsync(VaultPath path, CancellationToken ct);

    Task<Result<FileStat>> StatAsync(VaultPath path, CancellationToken ct);

    Task<bool> ExistsAsync(VaultPath path, CancellationToken ct);
}

/// <summary>Write access. Every write is atomic (temp file in the same folder + rename) and creates parent folders.</summary>
public interface IFileWriter
{
    /// <summary>
    /// Writes a file atomically. With <paramref name="expectedSha"/>, fails with <c>fs.concurrent_modification</c>
    /// unless the file exists and its current content has that blob SHA (optimistic concurrency).
    /// </summary>
    Task<Result<Unit>> WriteAllBytesAsync(VaultPath path, ReadOnlyMemory<byte> content, BlobSha? expectedSha, CancellationToken ct);

    /// <summary>Atomically creates a file that must not exist yet (<c>fs.already_exists</c> otherwise).</summary>
    Task<Result<Unit>> CreateNewAsync(VaultPath path, ReadOnlyMemory<byte> content, CancellationToken ct);

    /// <summary>Creates a folder (and its parents); succeeds when it already exists.</summary>
    Task<Result<Unit>> CreateDirectoryAsync(VaultPath path, CancellationToken ct);

    /// <summary>Moves a file or folder. Fails with <c>fs.already_exists</c> if the target exists, except for a
    /// case-only rename of the same entry; <c>fs.not_found</c> if the source does not exist.</summary>
    Task<Result<Unit>> MoveAsync(VaultPath from, VaultPath to, CancellationToken ct);

    /// <summary>Physically deletes a file, or a folder with its content.</summary>
    Task<Result<Unit>> DeleteAsync(VaultPath path, CancellationToken ct);

    /// <summary>Deletes a folder only if it is empty; succeeds (no-op) otherwise or when it does not exist.</summary>
    Task<Result<Unit>> DeleteDirectoryIfEmptyAsync(VaultPath path, CancellationToken ct);
}

/// <summary>Enumeration of vault entries (hidden ones included; Basalt's temporary files never appear).</summary>
public interface IFileEnumerator
{
    IAsyncEnumerable<FileEntry> EnumerateAsync(VaultPath root, bool recursive, CancellationToken ct);
}

/// <summary>Watches external changes: debounced, normalised to <see cref="VaultPath"/>, temp files filtered.</summary>
public interface IFileWatcher : IAsyncDisposable
{
    IAsyncEnumerable<FileSystemChange> WatchAsync(CancellationToken ct);
}

/// <summary>The vault file system: all capabilities rooted at one folder.</summary>
public interface IVaultFileSystem : IFileReader, IFileWriter, IFileEnumerator
{
    string RootLocation { get; }

    bool IsCaseSensitive { get; }

    IFileWatcher CreateWatcher();
}
