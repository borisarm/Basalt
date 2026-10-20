using System.Text;
using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Templates;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Notes;

/// <summary>
/// Creates a note (F-002.1/F-002.2): in <paramref name="Folder"/> or the vault's new-note folder, titled
/// <paramref name="Title"/> or "Untitled", "Untitled 1"..., optionally from a template.
/// </summary>
/// <returns><c>path.already_exists</c> when an explicit title is taken, <c>path.invalid_segment</c>, <c>entry.not_found</c> (template).</returns>
public sealed record CreateNote(VaultId VaultId, VaultPath? Folder = null, string? Title = null, VaultPath? TemplatePath = null) : ICommand<VaultPath>, IVaultRequest;

public sealed class CreateNoteHandler(IVaultSessions sessions, IEventBus events, IClock clock, ICultureProvider culture) : ICommandHandler<CreateNote, VaultPath>
{
    public const string DefaultTitle = "Untitled";

    public async Task<Result<VaultPath>> HandleAsync(CreateNote command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = sessions.Get(command.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var fs = session.Value.FileSystem;
        var folder = command.Folder ?? VaultPath.Create(session.Value.Settings.NewNoteFolder).Match(p => p, _ => VaultPath.Root);
        Result<VaultPath> path;
        if (string.IsNullOrWhiteSpace(command.Title))
        {
            path = await NoteFiles.UniqueAsync(fs, folder, DefaultTitle, "md", ct).ConfigureAwait(false);
        }
        else
        {
            path = folder.Combine(command.Title.Trim() + ".md");
            if (path.IsSuccess && await fs.ExistsAsync(path.Value, ct).ConfigureAwait(false))
            {
                return NoteFiles.AlreadyExists(path.Value);
            }
        }

        if (path.IsFailure)
        {
            return path.Error;
        }

        var text = string.Empty;
        if (command.TemplatePath is { } templatePath)
        {
            var template = await NoteFiles.ReadAsync(fs, templatePath, ct).ConfigureAwait(false);
            if (template.IsFailure)
            {
                return template.Error;
            }

            var now = TimeZoneInfo.ConvertTime(clock.UtcNow, clock.LocalZone).DateTime;
            text = TemplateRenderer.Render(template.Value.Content.Text, new TemplateContext(path.Value.NameWithoutExtension, now, culture.Current));
        }

        var created = await fs.CreateNewAsync(path.Value, Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return created.Error.Code == ErrorCodes.FsAlreadyExists ? NoteFiles.AlreadyExists(path.Value) : created.Error;
        }

        await events.PublishAsync(new EntriesCreated(command.VaultId, [path.Value]), ct).ConfigureAwait(false);
        return path.Value;
    }
}

/// <summary>Creates a folder.</summary>
/// <returns><c>path.already_exists</c> when an entry with that name exists.</returns>
public sealed record CreateFolder(VaultId VaultId, VaultPath Path) : ICommand<VaultPath>, IVaultRequest;

public sealed class CreateFolderHandler(IVaultSessions sessions, IEventBus events) : ICommandHandler<CreateFolder, VaultPath>
{
    public async Task<Result<VaultPath>> HandleAsync(CreateFolder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = sessions.Get(command.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        if (command.Path.IsRoot || await session.Value.FileSystem.ExistsAsync(command.Path, ct).ConfigureAwait(false))
        {
            return NoteFiles.AlreadyExists(command.Path);
        }

        var created = await session.Value.FileSystem.CreateDirectoryAsync(command.Path, ct).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return created.Error;
        }

        await events.PublishAsync(new EntriesCreated(command.VaultId, [command.Path]), ct).ConfigureAwait(false);
        return command.Path;
    }
}
