using System.Globalization;
using Basalt.Application.Errors;
using Basalt.Application.Ports.Platform;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Microsoft.VisualBasic.FileIO;

namespace Basalt.Adapters.Platform;

/// <summary>Windows recycle bin (shell file operation, no confirmation UI). Unavailable on other systems.</summary>
internal sealed class SystemTrash : ISystemTrash
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    public Task<Result<Unit>> MoveToTrashAsync(string rootLocation, VaultPath path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rootLocation);
        if (!IsAvailable)
        {
            return Task.FromResult<Result<Unit>>(new Error(ErrorCodes.FsTrashUnavailable, "This system has no recycle bin."));
        }

        var full = Path.Combine([rootLocation, .. path.Segments]);
        try
        {
            if (Directory.Exists(full))
            {
                FileSystem.DeleteDirectory(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            }
            else if (File.Exists(full))
            {
                FileSystem.DeleteFile(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            }
            else
            {
                return Task.FromResult<Result<Unit>>(new Error(ErrorCodes.FsNotFound, "The entry does not exist.").With("path", path.ToString()));
            }

            return Task.FromResult(Result.Success());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return Task.FromResult<Result<Unit>>(new Error(ErrorCodes.FsIo, "Could not move the entry to the recycle bin.")
                .With("path", path.ToString())
                .With("reason", e.GetType().Name));
        }
    }
}

/// <summary>The current UI culture of the process (the app sets it from the language setting at startup).</summary>
internal sealed class SystemCultureProvider : ICultureProvider
{
    public CultureInfo Current => CultureInfo.CurrentCulture;
}
