using Basalt.Application.Abstractions;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Notes;

public enum DeleteMode
{
    /// <summary>Move to <c>.trash/</c> inside the vault (default, F-002.6).</summary>
    Trash,

    /// <summary>The operating system's recycle bin; falls back to <see cref="Trash"/> where unavailable.</summary>
    SystemTrash,

    /// <summary>Physical deletion.</summary>
    Permanent,
}

/// <summary>Deletes notes, attachments or folders. Trash moves keep the relative path (".trash/x/A.md", "A 2.md"...).</summary>
public sealed record DeleteEntries(VaultId VaultId, IReadOnlyList<VaultPath> Paths, DeleteMode Mode = DeleteMode.Trash) : ICommand<Unit>, IVaultRequest;

public sealed class DeleteEntriesHandler(IVaultSessions sessions, IEventBus events, ISystemTrash? systemTrash = null) : ICommandHandler<DeleteEntries, Unit>
{
    private static readonly VaultPath TrashFolder = VaultPath.Create(FileClassifier.TrashFolder).Value;

    public async Task<Result<Unit>> HandleAsync(DeleteEntries command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = sessions.Get(command.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var fs = session.Value.FileSystem;
        var deleted = new List<VaultPath>();
        foreach (var path in command.Paths)
        {
            if (!await fs.ExistsAsync(path, ct).ConfigureAwait(false))
            {
                return NoteFiles.NotFound(path);
            }

            Result<Unit> result;
            if (command.Mode == DeleteMode.Permanent || path.IsDescendantOf(TrashFolder))
            {
                // Deleting from the trash itself is permanent.
                result = await fs.DeleteAsync(path, ct).ConfigureAwait(false);
            }
            else if (command.Mode == DeleteMode.SystemTrash && systemTrash is { IsAvailable: true })
            {
                result = await systemTrash.MoveToTrashAsync(fs.RootLocation, path, ct).ConfigureAwait(false);
            }
            else
            {
                var target = await NoteFiles.UniqueFromTwoAsync(fs, TrashFolder.Combine(path.ToString()).Value, ct).ConfigureAwait(false);
                result = target.IsFailure ? Result.Failure(target.Error) : await fs.MoveAsync(path, target.Value, ct).ConfigureAwait(false);
            }

            if (result.IsFailure)
            {
                await PublishAsync(command.VaultId, deleted, ct).ConfigureAwait(false);
                return result;
            }

            deleted.Add(path);
        }

        await PublishAsync(command.VaultId, deleted, ct).ConfigureAwait(false);
        return Result.Success();
    }

    private Task PublishAsync(VaultId vault, List<VaultPath> deleted, CancellationToken ct) =>
        deleted.Count == 0 ? Task.CompletedTask : events.PublishAsync(new EntriesDeleted(vault, deleted), ct).AsTask();
}
