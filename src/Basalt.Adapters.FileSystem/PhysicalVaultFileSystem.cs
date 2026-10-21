using System.Runtime.CompilerServices;
using System.Text;
using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Adapters.FileSystem;

/// <summary>
/// <see cref="IVaultFileSystem"/> over System.IO (spec 03 §1.1). Writes are atomic (temp file in the same folder,
/// flushed to disk, then renamed over the target); I/O exceptions become <see cref="Error"/>s at this boundary.
/// </summary>
internal sealed class PhysicalVaultFileSystem : IVaultFileSystem
{
    private static readonly EnumerationOptions ShallowOptions = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        MatchCasing = MatchCasing.PlatformDefault,
    };

    private readonly string _root;

    public PhysicalVaultFileSystem(string rootLocation, bool isCaseSensitive)
    {
        _root = Path.GetFullPath(rootLocation);
        RootLocation = rootLocation;
        IsCaseSensitive = isCaseSensitive;
    }

    public string RootLocation { get; }

    public bool IsCaseSensitive { get; }

    /// <summary>Test hook invoked after the temporary file is written and before it replaces the target.</summary>
    internal Func<string, Task>? BeforeCommit { get; set; }

    public IFileWatcher CreateWatcher() => new PhysicalFileWatcher(_root);

    public Task<Result<byte[]>> ReadAllBytesAsync(VaultPath path, CancellationToken ct) =>
        Guard(async () =>
        {
            var full = Resolve(path);
            if (Directory.Exists(full))
            {
                return Result.Failure<byte[]>(IsDirectoryError());
            }

            return Result.Success(await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false));
        });

    public async Task<Result<string>> ReadAllTextAsync(VaultPath path, CancellationToken ct) =>
        (await ReadAllBytesAsync(path, ct).ConfigureAwait(false)).Map(bytes => NoteContent.FromBytes(bytes).Text);

    public Task<Result<Stream>> OpenReadAsync(VaultPath path, CancellationToken ct) =>
        Guard(() =>
        {
            var full = Resolve(path);
            if (Directory.Exists(full))
            {
                return Task.FromResult(Result.Failure<Stream>(IsDirectoryError()));
            }

            Stream stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
            return Task.FromResult(Result.Success(stream));
        });

    public Task<Result<FileStat>> StatAsync(VaultPath path, CancellationToken ct) =>
        Guard(() =>
        {
            var full = Resolve(path);
            if (Directory.Exists(full))
            {
                return Task.FromResult(Result.Success(new FileStat(0, new DateTimeOffset(Directory.GetLastWriteTimeUtc(full), TimeSpan.Zero), true)));
            }

            var info = new FileInfo(full);
            return Task.FromResult(info.Exists
                ? Result.Success(new FileStat(info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), false))
                : Result.Failure<FileStat>(NotFound(path)));
        });

    public Task<bool> ExistsAsync(VaultPath path, CancellationToken ct)
    {
        var full = Resolve(path);
        return Task.FromResult(File.Exists(full) || Directory.Exists(full));
    }

    public Task<Result<Unit>> WriteAllBytesAsync(VaultPath path, ReadOnlyMemory<byte> content, BlobSha? expectedSha, CancellationToken ct) =>
        Guard(async () =>
        {
            var full = Resolve(path);
            if (Directory.Exists(full))
            {
                return Result.Failure(IsDirectoryError());
            }

            if (expectedSha is { } expected)
            {
                if (!File.Exists(full) || GitBlobHasher.Compute(await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false)) != expected)
                {
                    return Result.Failure(new Error(ErrorCodes.FsConcurrentModification, "The file changed since it was read."));
                }
            }

            var parent = EnsureParent(full);
            if (parent.IsFailure)
            {
                return parent;
            }

            return await AtomicWriteAsync(ExistingName(full), content, overwrite: true, ct).ConfigureAwait(false);
        });

    public Task<Result<Unit>> CreateNewAsync(VaultPath path, ReadOnlyMemory<byte> content, CancellationToken ct) =>
        Guard(async () =>
        {
            var full = Resolve(path);
            if (File.Exists(full) || Directory.Exists(full))
            {
                return Result.Failure(AlreadyExists());
            }

            var parent = EnsureParent(full);
            return parent.IsFailure ? parent : await AtomicWriteAsync(full, content, overwrite: false, ct).ConfigureAwait(false);
        });

    public Task<Result<Unit>> CreateDirectoryAsync(VaultPath path, CancellationToken ct) =>
        Guard(() =>
        {
            var full = Resolve(path);
            if (File.Exists(full))
            {
                return Task.FromResult(Result.Failure(new Error(ErrorCodes.FsNotADirectory, "A file exists with that name.")));
            }

            var parent = EnsureParent(full);
            if (parent.IsFailure)
            {
                return Task.FromResult(parent);
            }

            Directory.CreateDirectory(full);
            return Task.FromResult(Result.Success());
        });

    public Task<Result<Unit>> MoveAsync(VaultPath from, VaultPath to, CancellationToken ct) =>
        Guard(() =>
        {
            var source = Resolve(from);
            var isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source))
            {
                return Task.FromResult(Result.Failure(NotFound(from)));
            }

            var target = Resolve(to);
            var caseOnly = !IsCaseSensitive && string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            {
                return Task.FromResult(Result.Failure(AlreadyExists()));
            }

            var parent = EnsureParent(target);
            if (parent.IsFailure)
            {
                return Task.FromResult(parent);
            }

            if (caseOnly)
            {
                // Case-insensitive file systems need an intermediate name to change only the case.
                var intermediate = Path.Combine(Path.GetDirectoryName(source)!, TempName());
                MoveEntry(source, intermediate, isDirectory);
                MoveEntry(intermediate, Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileName(target)), isDirectory);
            }
            else
            {
                MoveEntry(source, target, isDirectory);
            }

            return Task.FromResult(Result.Success());
        });

    public Task<Result<Unit>> DeleteAsync(VaultPath path, CancellationToken ct) =>
        Guard(() =>
        {
            var full = Resolve(path);
            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
            else if (File.Exists(full))
            {
                File.Delete(full);
            }
            else
            {
                return Task.FromResult(Result.Failure(NotFound(path)));
            }

            return Task.FromResult(Result.Success());
        });

    public Task<Result<Unit>> DeleteIfUnchangedAsync(VaultPath path, BlobSha expectedSha, CancellationToken ct) =>
        Guard(async () =>
        {
            var full = Resolve(path);
            if (Directory.Exists(full))
            {
                return Result.Failure(IsDirectoryError());
            }

            if (!File.Exists(full))
            {
                return Result.Failure(NotFound(path));
            }

            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var actual = await GitBlobHasher.ComputeAsync(stream, stream.Length, ct).ConfigureAwait(false);
                if (actual != expectedSha)
                {
                    return Result.Failure(ConcurrentModification(path));
                }
            }

            File.Delete(full);
            return Result.Success();
        });

    public Task<Result<Unit>> DeleteDirectoryIfEmptyAsync(VaultPath path, CancellationToken ct) =>
        Guard(() =>
        {
            var full = Resolve(path);
            if (!path.IsRoot && Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
            {
                Directory.Delete(full);
            }

            return Task.FromResult(Result.Success());
        });

    public async IAsyncEnumerable<FileEntry> EnumerateAsync(VaultPath root, bool recursive, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        var start = new DirectoryInfo(Resolve(root));
        if (!start.Exists)
        {
            yield break;
        }

        var pending = new Stack<(DirectoryInfo Directory, VaultPath Path)>();
        pending.Push((start, root));
        while (pending.Count > 0)
        {
            var (directory, directoryPath) = pending.Pop();
            foreach (var info in directory.EnumerateFileSystemInfos("*", ShallowOptions))
            {
                ct.ThrowIfCancellationRequested();
                if (VaultFileConventions.IsTemporaryFile(info.Name) || directoryPath.Combine(info.Name) is not { IsSuccess: true } childResult)
                {
                    continue; // names that are not valid vault paths cannot be addressed and are skipped
                }

                var child = childResult.Value;
                var isDirectory = info is DirectoryInfo;
                var isSymbolicLink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                yield return new FileEntry(
                    child,
                    isDirectory,
                    isDirectory ? 0 : ((FileInfo)info).Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    isSymbolicLink);
                if (isDirectory && !isSymbolicLink && recursive)
                {
                    pending.Push(((DirectoryInfo)info, child));
                }
            }
        }
    }

    /// <summary>
    /// Maps a vault path to a full path. When the direct path does not exist, segments are matched against directory
    /// entries normalised to NFC (and case-folded on case-insensitive volumes), so files created elsewhere with NFD
    /// names (macOS) remain addressable.
    /// </summary>
    internal string Resolve(VaultPath path)
    {
        var direct = Path.Combine([_root, .. path.Segments]);
        if (path.IsRoot || File.Exists(direct) || Directory.Exists(direct))
        {
            return direct;
        }

        var current = _root;
        foreach (var segment in path.Segments)
        {
            var match = Directory.Exists(current)
                ? Directory.EnumerateFileSystemEntries(current).Select(Path.GetFileName).FirstOrDefault(name => SameName(name!, segment))
                : null;
            current = Path.Combine(current, match ?? segment);
        }

        return current;
    }

    // On case-insensitive volumes an overwrite must keep the existing file name ("Readme.md" stays "Readme.md").
    private string ExistingName(string full)
    {
        if (IsCaseSensitive || !File.Exists(full))
        {
            return full;
        }

        var directory = Path.GetDirectoryName(full)!;
        var name = Path.GetFileName(full);
        var actual = Directory.EnumerateFiles(directory).Select(Path.GetFileName)
            .FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        return actual is null ? full : Path.Combine(directory, actual);
    }

    private bool SameName(string onDisk, string segment) =>
        string.Equals(onDisk.Normalize(NormalizationForm.FormC), segment, IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private async Task<Result<Unit>> AtomicWriteAsync(string full, ReadOnlyMemory<byte> content, bool overwrite, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetDirectoryName(full)!, TempName());
        try
        {
            var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (BeforeCommit is { } hook)
            {
                await hook(temp).ConfigureAwait(false);
            }

            try
            {
                File.Move(temp, full, overwrite);
            }
            catch (IOException) when (!overwrite && File.Exists(full))
            {
                return Result.Failure(AlreadyExists());
            }

            return Result.Success();
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static Result<Unit> EnsureParent(string full)
    {
        var parent = Path.GetDirectoryName(full)!;
        for (var directory = parent; directory is not null && !Directory.Exists(directory); directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(directory))
            {
                return Result.Failure(new Error(ErrorCodes.FsNotADirectory, "A parent of the path is a file."));
            }
        }

        Directory.CreateDirectory(parent);
        return Result.Success();
    }

    private static void MoveEntry(string from, string to, bool isDirectory)
    {
        if (isDirectory)
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to);
        }
    }

    private static string TempName() => $"{VaultFileConventions.TempFilePrefix}{Guid.NewGuid():N}{VaultFileConventions.TempFileSuffix}";

    private static async Task<Result<T>> Guard<T>(Func<Task<Result<T>>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            return new Error(ErrorCodes.FsNotFound, ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            return new Error(ErrorCodes.FsNotFound, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new Error(ErrorCodes.FsAccessDenied, ex.Message);
        }
        catch (IOException ex)
        {
            return new Error(ErrorCodes.FsIo, ex.Message);
        }
    }

    private static Error NotFound(VaultPath path) =>
        new Error(ErrorCodes.FsNotFound, "The file or folder does not exist.").With("path", path.ToString());

    private static Error AlreadyExists() => new(ErrorCodes.FsAlreadyExists, "The target already exists.");

    private static Error ConcurrentModification(VaultPath path) =>
        new Error(ErrorCodes.FsConcurrentModification, "The file changed since it was read.").With("path", path.ToString());

    private static Error IsDirectoryError() => new(ErrorCodes.FsIsDirectory, "The path is a directory.");
}
