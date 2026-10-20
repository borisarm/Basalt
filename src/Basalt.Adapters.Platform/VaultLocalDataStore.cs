using System.Globalization;
using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;

namespace Basalt.Adapters.Platform;

/// <summary><c>{AppData}/vaults/{vaultId}/</c> (spec 08 §1).</summary>
internal sealed class VaultLocalDataStore(IPlatformPaths paths) : IVaultLocalDataStore
{
    public string DirectoryOf(VaultId vault) =>
        Path.Combine(paths.AppDataDirectory, "vaults", vault.Value.ToString("D", CultureInfo.InvariantCulture));

    public Task<Result<Unit>> DeleteAsync(VaultId vault, CancellationToken ct)
    {
        try
        {
            var directory = DirectoryOf(vault);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return Task.FromResult(Result.Success());
        }
        catch (IOException ex)
        {
            return Task.FromResult(Result.Failure(new Error(ErrorCodes.StorageIo, ex.Message)));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(Result.Failure(new Error(ErrorCodes.StorageIo, ex.Message)));
        }
    }
}
