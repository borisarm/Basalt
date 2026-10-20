using System.Text.RegularExpressions;
using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Indexing;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.Markdown;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Links;
using Basalt.Domain.Notes;
using Basalt.Domain.Text;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Notes;

/// <summary>Saves an attachment next to the configured attachment folder and returns the embed to insert (F-003.5).</summary>
/// <param name="FileName">Suggested name ("Pasted image 20261019154233.png"); made unique if taken.</param>
public sealed record ImportAttachment(VaultId VaultId, VaultPath SourceNote, string FileName, byte[] Bytes) : ICommand<ImportedAttachment>, IVaultRequest;

public sealed record ImportedAttachment(VaultPath Path, string EmbedText);

public sealed class ImportAttachmentHandler(IVaultSessions sessions, IEventBus events) : ICommandHandler<ImportAttachment, ImportedAttachment>
{
    public async Task<Result<ImportedAttachment>> HandleAsync(ImportAttachment command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = sessions.Get(command.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var folder = AttachmentFolder(session.Value.Settings.AttachmentFolder, command.SourceNote);
        if (folder.IsFailure)
        {
            return folder.Error;
        }

        var requested = VaultPath.Create(command.FileName);
        if (requested.IsFailure || requested.Value.Segments.Count != 1)
        {
            return requested.IsFailure ? requested.Error : NoteFiles.AlreadyExists(requested.Value);
        }

        var fs = session.Value.FileSystem;
        var path = await NoteFiles.UniqueAsync(fs, folder.Value, requested.Value.NameWithoutExtension, requested.Value.Extension, ct).ConfigureAwait(false);
        if (path.IsFailure)
        {
            return path.Error;
        }

        var written = await fs.CreateNewAsync(path.Value, command.Bytes, ct).ConfigureAwait(false);
        if (written.IsFailure)
        {
            return written.Error;
        }

        await events.PublishAsync(new EntriesCreated(command.VaultId, [path.Value]), ct).ConfigureAwait(false);
        var catalog = new PathListCatalog((session.Value.TryGet<IMetadataCache>()?.Current.Files.Keys ?? []).Append(path.Value));
        return new ImportedAttachment(path.Value, LinkFormatter.FormatLink(path.Value, command.SourceNote, session.Value.Settings, catalog, embed: true));
    }

    // Obsidian semantics: "" = vault root, "./" = the note's folder, "./sub" = a subfolder of it, other = vault folder.
    private static Result<VaultPath> AttachmentFolder(string setting, VaultPath note) =>
        setting.StartsWith("./", StringComparison.Ordinal) || setting == "."
            ? note.Parent.Combine(setting)
            : VaultPath.Create(setting);
}

/// <summary>Sets a frontmatter property preserving the rest of the document (F-010.1).</summary>
/// <returns><c>note.frontmatter_invalid</c> when the YAML cannot be parsed (F-010.2: the source editor still works).</returns>
public sealed record SetFrontmatterProperty(VaultId VaultId, VaultPath Path, string Key, StructuredValue Value) : ICommand<Unit>, IVaultRequest;

public sealed class SetFrontmatterPropertyHandler(IVaultSessions sessions, IFrontmatterEditor editor, IMarkdownAnalyzer analyzer, IEventBus events)
    : ICommandHandler<SetFrontmatterProperty, Unit>
{
    public Task<Result<Unit>> HandleAsync(SetFrontmatterProperty command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return NoteEditing.EditAsync(sessions, events, command.VaultId, command.Path, text =>
            analyzer.Analyze(text).Frontmatter is { IsValid: false }
                ? new Error(ErrorCodes.NoteFrontmatterInvalid, "The frontmatter is not valid YAML.")
                : editor.SetProperty(text, command.Key, command.Value), ct);
    }
}

public sealed record RemoveFrontmatterProperty(VaultId VaultId, VaultPath Path, string Key) : ICommand<Unit>, IVaultRequest;

public sealed class RemoveFrontmatterPropertyHandler(IVaultSessions sessions, IFrontmatterEditor editor, IMarkdownAnalyzer analyzer, IEventBus events)
    : ICommandHandler<RemoveFrontmatterProperty, Unit>
{
    public Task<Result<Unit>> HandleAsync(RemoveFrontmatterProperty command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return NoteEditing.EditAsync(sessions, events, command.VaultId, command.Path, text =>
            analyzer.Analyze(text).Frontmatter is { IsValid: false }
                ? new Error(ErrorCodes.NoteFrontmatterInvalid, "The frontmatter is not valid YAML.")
                : editor.RemoveProperty(text, command.Key), ct);
    }
}

/// <summary>Toggles the task on a 0-based line ("- [ ]" ↔ "- [x]").</summary>
/// <returns><c>note.not_a_task</c> when that line is not a task.</returns>
public sealed record ToggleTask(VaultId VaultId, VaultPath Path, int Line) : ICommand<Unit>, IVaultRequest;

public sealed partial class ToggleTaskHandler(IVaultSessions sessions, IEventBus events) : ICommandHandler<ToggleTask, Unit>
{
    public Task<Result<Unit>> HandleAsync(ToggleTask command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return NoteEditing.EditAsync(sessions, events, command.VaultId, command.Path, text =>
        {
            var lines = TextLines.Split(text).ToList();
            if (command.Line < 0 || command.Line >= lines.Count || TaskRegex().Match(lines[command.Line]) is not { Success: true } match)
            {
                return new Error(ErrorCodes.NoteNotATask, "The line is not a task.");
            }

            var status = match.Groups["status"];
            var toggled = status.Value == " " ? "x" : " ";
            lines[command.Line] = string.Concat(lines[command.Line].AsSpan(0, status.Index), toggled, lines[command.Line].AsSpan(status.Index + 1));
            return string.Concat(lines);
        }, ct);
    }

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s+\[(?<status>.)\]")]
    private static partial Regex TaskRegex();
}

/// <summary>Read → transform → write with optimistic concurrency, keeping line endings and BOM.</summary>
internal static class NoteEditing
{
    public static async Task<Result<Unit>> EditAsync(IVaultSessions sessions, IEventBus events, VaultId vault, VaultPath path, Func<string, Result<string>> transform, CancellationToken ct)
    {
        var session = sessions.Get(vault);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var file = await NoteFiles.ReadAsync(session.Value.FileSystem, path, ct).ConfigureAwait(false);
        if (file.IsFailure)
        {
            return file.Error;
        }

        var edited = transform(file.Value.Content.Text);
        if (edited.IsFailure)
        {
            return edited.Error;
        }

        if (edited.Value == file.Value.Content.Text)
        {
            return Result.Success();
        }

        var bytes = file.Value.Content.WithText(edited.Value).ToBytes();
        var written = await session.Value.FileSystem.WriteAllBytesAsync(path, bytes, file.Value.Sha, ct).ConfigureAwait(false);
        if (written.IsFailure)
        {
            return written.Error.Code == ErrorCodes.FsConcurrentModification
                ? new Error(ErrorCodes.NoteModifiedExternally, "The note changed on disk; try again.")
                : written.Error;
        }

        await events.PublishAsync(new NoteSaved(vault, path), ct).ConfigureAwait(false);
        return Result.Success();
    }
}
