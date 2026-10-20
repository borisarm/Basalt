using Basalt.Application.Abstractions;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Notes;

/// <summary>
/// Renames a note, attachment or folder (F-002.3/F-002.4, F-004.4/F-004.5). For files without an extension in
/// <paramref name="NewName"/> the original extension is kept ("B" → "B.md").
/// </summary>
/// <returns><c>path.already_exists</c>, <c>entry.not_found</c>, <c>path.invalid_segment</c>.</returns>
public sealed record RenameEntry(VaultId VaultId, VaultPath Path, string NewName) : ICommand<VaultPath>, IVaultRequest;

public sealed class RenameEntryHandler(IVaultSessions sessions, EntryMover mover) : ICommandHandler<RenameEntry, VaultPath>
{
    public async Task<Result<VaultPath>> HandleAsync(RenameEntry command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = sessions.Get(command.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var stat = await session.Value.FileSystem.StatAsync(command.Path, ct).ConfigureAwait(false);
        if (stat.IsFailure)
        {
            return NoteFiles.NotFound(command.Path);
        }

        var name = command.NewName.Trim();
        if (!stat.Value.IsDirectory && command.Path.Extension.Length > 0 && VaultPath.Create(name) is { IsSuccess: true } typed && typed.Value.Extension != command.Path.Extension)
        {
            name += command.Path.Name[command.Path.Name.LastIndexOf('.')..];
        }

        var target = command.Path.WithName(name);
        if (target.IsFailure)
        {
            return target.Error;
        }

        if (target.Value == command.Path)
        {
            return command.Path;
        }

        var moved = await mover.MoveAsync(session.Value, [(command.Path, target.Value)], ct).ConfigureAwait(false);
        return moved.Map(_ => target.Value);
    }
}

/// <summary>Moves entries into a folder (F-002.5). Entries already in that folder are left alone.</summary>
public sealed record MoveEntries(VaultId VaultId, IReadOnlyList<VaultPath> Paths, VaultPath TargetFolder) : ICommand<IReadOnlyList<VaultPath>>, IVaultRequest;

public sealed class MoveEntriesHandler(IVaultSessions sessions, EntryMover mover) : ICommandHandler<MoveEntries, IReadOnlyList<VaultPath>>
{
    public async Task<Result<IReadOnlyList<VaultPath>>> HandleAsync(MoveEntries command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = sessions.Get(command.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var moves = new List<(VaultPath From, VaultPath To)>();
        foreach (var path in command.Paths.Where(p => p.Parent != command.TargetFolder))
        {
            var target = command.TargetFolder.Combine(path.Name);
            if (target.IsFailure)
            {
                return target.Error;
            }

            moves.Add((path, target.Value));
        }

        if (moves.Count == 0)
        {
            return Result.Success<IReadOnlyList<VaultPath>>(command.Paths);
        }

        var moved = await mover.MoveAsync(session.Value, moves, ct).ConfigureAwait(false);
        return moved.Map<IReadOnlyList<VaultPath>>(outcome =>
        {
            var targets = outcome.Moves.ToDictionary(m => m.From, m => m.To);
            return [.. command.Paths.Select(p => targets.GetValueOrDefault(p, p))];
        });
    }
}
