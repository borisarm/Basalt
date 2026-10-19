using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Domain.Common;

namespace Basalt.Adapters.FileSystem;

/// <summary>Opens <see cref="PhysicalVaultFileSystem"/> instances, probing the volume's case sensitivity.</summary>
internal sealed class PhysicalVaultFileSystemFactory : IVaultFileSystemFactory
{
    public Task<Result<IVaultFileSystem>> OpenAsync(string rootLocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rootLocation);
        if (!Directory.Exists(rootLocation))
        {
            return Task.FromResult(Result.Failure<IVaultFileSystem>(
                new Error(ErrorCodes.FsNotFound, "The vault folder does not exist.")));
        }

        try
        {
            return Task.FromResult(Result.Success<IVaultFileSystem>(new PhysicalVaultFileSystem(rootLocation, ProbeCaseSensitivity(rootLocation))));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(Result.Failure<IVaultFileSystem>(new Error(ErrorCodes.FsAccessDenied, ex.Message)));
        }
        catch (IOException ex)
        {
            return Task.FromResult(Result.Failure<IVaultFileSystem>(new Error(ErrorCodes.FsIo, ex.Message)));
        }
    }

    // Creates a lower-case probe file and checks whether its upper-case name resolves to it.
    internal static bool ProbeCaseSensitivity(string root)
    {
        var probe = Path.Combine(root, $"{VaultFileConventions.TempFilePrefix}probe-{Guid.NewGuid():N}{VaultFileConventions.TempFileSuffix}");
        File.WriteAllBytes(probe, []);
        try
        {
            return !File.Exists(Path.Combine(root, Path.GetFileName(probe).ToUpperInvariant()));
        }
        finally
        {
            File.Delete(probe);
        }
    }
}
