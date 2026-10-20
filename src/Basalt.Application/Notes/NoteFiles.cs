using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Links;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Application.Notes;

/// <summary>A note as read from disk: content (with its line endings/BOM), raw bytes and blob SHA.</summary>
public sealed record NoteFile(VaultPath Path, NoteContent Content, byte[] Bytes, BlobSha Sha);

/// <summary>Shared helpers of the file use cases.</summary>
internal static class NoteFiles
{
    public static async Task<Result<NoteFile>> ReadAsync(IVaultFileSystem fs, VaultPath path, CancellationToken ct)
    {
        var bytes = await fs.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (bytes.IsFailure)
        {
            return bytes.Error.Code == ErrorCodes.FsNotFound ? NotFound(path) : bytes.Error;
        }

        return new NoteFile(path, NoteContent.FromBytes(bytes.Value), bytes.Value, GitBlobHasher.Compute(bytes.Value));
    }

    public static Error NotFound(VaultPath path) =>
        new Error(ErrorCodes.EntryNotFound, "The file or folder does not exist.").With("path", path.ToString());

    public static Error AlreadyExists(VaultPath path) =>
        new Error(ErrorCodes.PathAlreadyExists, "A file or folder with that name already exists.").With("path", path.ToString());

    /// <summary>"Untitled.md", "Untitled 1.md", ... — the first name not taken (case-insensitively).</summary>
    public static async Task<Result<VaultPath>> UniqueAsync(IVaultFileSystem fs, VaultPath folder, string stem, string extension, CancellationToken ct)
    {
        var suffix = extension.Length == 0 ? string.Empty : "." + extension;
        for (var n = 0; n < 10_000; n++)
        {
            var candidate = folder.Combine(n == 0 ? stem + suffix : $"{stem} {n}{suffix}");
            if (candidate.IsFailure)
            {
                return candidate.Error;
            }

            if (!await fs.ExistsAsync(candidate.Value, ct).ConfigureAwait(false))
            {
                return candidate.Value;
            }
        }

        return AlreadyExists(folder);
    }

    /// <summary>Trash-style uniqueness: "A.md", "A 2.md", "A 3.md".</summary>
    public static async Task<Result<VaultPath>> UniqueFromTwoAsync(IVaultFileSystem fs, VaultPath path, CancellationToken ct)
    {
        if (!await fs.ExistsAsync(path, ct).ConfigureAwait(false))
        {
            return path;
        }

        var extension = path.Extension.Length == 0 ? string.Empty : "." + path.Name[(path.Name.LastIndexOf('.') + 1)..];
        for (var n = 2; n < 10_000; n++)
        {
            var candidate = path.WithName($"{path.NameWithoutExtension} {n}{extension}");
            if (candidate.IsFailure)
            {
                return candidate.Error;
            }

            if (!await fs.ExistsAsync(candidate.Value, ct).ConfigureAwait(false))
            {
                return candidate.Value;
            }
        }

        return AlreadyExists(path);
    }

    public static Result<VaultSession> Session(IVaultSessions sessions, Domain.Vaults.VaultId id) => sessions.Get(id);
}

/// <summary>A catalog over an explicit list of paths (e.g. the vault as it will be after a rename).</summary>
internal sealed class PathListCatalog : IVaultFileCatalog
{
    private readonly ILookup<string, VaultPath> _byName;
    private readonly ILookup<string, VaultPath> _byPath;

    public PathListCatalog(IEnumerable<VaultPath> paths)
    {
        var list = paths.ToList();
        _byName = list.ToLookup(p => p.Name.ToUpperInvariant(), StringComparer.Ordinal);
        _byPath = list.ToLookup(p => p.CaseFoldKey, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<VaultPath> FindByName(string fileName) => [.. _byName[fileName.ToUpperInvariant()]];

    public IReadOnlyCollection<VaultPath> FindByPath(VaultPath path) => [.. _byPath[path.CaseFoldKey]];
}
