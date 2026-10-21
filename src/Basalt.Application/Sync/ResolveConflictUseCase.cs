using System.Text;
using System.Text.RegularExpressions;
using Basalt.Application.Abstractions;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Sync;

public enum ConflictResolutionKind
{
    KeepLocal,
    KeepRemote,
    KeepBoth,
    Merged,
}

/// <summary>Resolves a conflict copy or a note with merge markers (spec 03 §2 and 06 §5).</summary>
public sealed record ResolveConflict(
    VaultId VaultId,
    VaultPath Path,
    ConflictResolutionKind Resolution,
    string? MergedText = null) : ICommand<Unit>, IVaultRequest;

public sealed partial class ResolveConflictHandler(IVaultSessions sessions)
    : ICommandHandler<ResolveConflict, Unit>
{
    public async Task<Result<Unit>> HandleAsync(ResolveConflict command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = sessions.Get(command.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var fs = session.Value.FileSystem;
        var copy = ConflictCopyPattern().IsMatch(command.Path.Name);
        var original = copy ? Original(command.Path) : command.Path;
        if (original.IsFailure)
        {
            return original.Error;
        }

        switch (command.Resolution)
        {
            case ConflictResolutionKind.KeepBoth:
                return Result.Success();
            case ConflictResolutionKind.KeepLocal when !copy:
                {
                    var text = await fs.ReadAllTextAsync(command.Path, ct).ConfigureAwait(false);
                    return text.IsFailure
                        ? text.Error
                        : await fs.WriteAllBytesAsync(command.Path, Encoding.UTF8.GetBytes(KeepSide(text.Value, local: true)), null, ct).ConfigureAwait(false);
                }
            case ConflictResolutionKind.KeepRemote when !copy:
                {
                    var text = await fs.ReadAllTextAsync(command.Path, ct).ConfigureAwait(false);
                    return text.IsFailure
                        ? text.Error
                        : await fs.WriteAllBytesAsync(command.Path, Encoding.UTF8.GetBytes(KeepSide(text.Value, local: false)), null, ct).ConfigureAwait(false);
                }
            case ConflictResolutionKind.Merged:
                {
                    if (command.MergedText is null)
                    {
                        return new Error(SyncErrors.MissingMergedText, "The merged text is required.");
                    }

                    var written = await fs.WriteAllBytesAsync(original.Value, Encoding.UTF8.GetBytes(command.MergedText), null, ct).ConfigureAwait(false);
                    if (written.IsFailure)
                    {
                        return written;
                    }

                    break;
                }
            case ConflictResolutionKind.KeepRemote:
                {
                    var content = await fs.ReadAllBytesAsync(command.Path, ct).ConfigureAwait(false);
                    if (content.IsFailure)
                    {
                        return content.Error;
                    }

                    var written = await fs.WriteAllBytesAsync(original.Value, content.Value, null, ct).ConfigureAwait(false);
                    if (written.IsFailure)
                    {
                        return written;
                    }

                    break;
                }
        }

        return copy
            ? await fs.DeleteAsync(command.Path, ct).ConfigureAwait(false)
            : Result.Success();
    }

    private static Result<VaultPath> Original(VaultPath path) =>
        path.WithName(ConflictCopyPattern().Replace(path.Name, "$1"));

    private static string KeepSide(string text, bool local)
    {
        var output = new List<string>();
        var inLocal = false;
        var inRemote = false;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("<<<<<<< ", StringComparison.Ordinal))
            {
                inLocal = true;
            }
            else if (inRemote && line.StartsWith(">>>>>>> ", StringComparison.Ordinal))
            {
                inRemote = false;
            }
            else if (inLocal && line.StartsWith("=======", StringComparison.Ordinal))
            {
                inLocal = false;
                inRemote = true;
            }
            else if ((inLocal && local) || (inRemote && !local) || (!inLocal && !inRemote))
            {
                output.Add(line);
            }
        }

        return string.Join('\n', output);
    }

    [GeneratedRegex(@" \(conflict [^)]+\)(\.[^.]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ConflictCopyPattern();
}
