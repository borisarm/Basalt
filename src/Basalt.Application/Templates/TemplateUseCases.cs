using System.Text;
using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Markdown;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Templates;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Templates;

/// <summary>The daily note of a date (F-009.1/F-009.2) and whether it was just created.</summary>
public sealed record DailyNote(VaultPath Path, bool Created);

/// <summary>Opens the daily note of <paramref name="Date"/> (today, in the local zone, by default), creating it from the template if missing.</summary>
/// <returns><c>entry.not_found</c> when the configured template does not exist (nothing is created).</returns>
public sealed record OpenDailyNote(VaultId VaultId, DateOnly? Date = null) : ICommand<DailyNote>, IVaultRequest;

public sealed class OpenDailyNoteHandler(IVaultSessions sessions, IEventBus events, IClock clock, ICultureProvider culture) : ICommandHandler<OpenDailyNote, DailyNote>
{
    public async Task<Result<DailyNote>> HandleAsync(OpenDailyNote command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = sessions.Get(command.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var settings = session.Value.Settings.DailyNotes;
        var now = TimeZoneInfo.ConvertTime(clock.UtcNow, clock.LocalZone).DateTime;
        var date = command.Date ?? DateOnly.FromDateTime(now);
        var path = DailyNotePolicy.PathFor(date, settings.Folder, settings.DateFormat, culture.Current);
        if (path.IsFailure)
        {
            return path.Error;
        }

        var fs = session.Value.FileSystem;
        if (await fs.ExistsAsync(path.Value, ct).ConfigureAwait(false))
        {
            return new DailyNote(path.Value, false);
        }

        var text = string.Empty;
        if (!string.IsNullOrWhiteSpace(settings.TemplatePath))
        {
            var template = await TemplateFiles.RenderAsync(fs, settings.TemplatePath, path.Value.NameWithoutExtension, date.ToDateTime(TimeOnly.FromDateTime(now)), culture.Current, ct).ConfigureAwait(false);
            if (template.IsFailure)
            {
                return template.Error;
            }

            text = template.Value;
        }

        var created = await fs.CreateNewAsync(path.Value, Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return created.Error.Code == ErrorCodes.FsAlreadyExists ? new DailyNote(path.Value, false) : created.Error;
        }

        await events.PublishAsync(new EntriesCreated(command.VaultId, [path.Value]), ct).ConfigureAwait(false);
        return new DailyNote(path.Value, true);
    }
}

/// <summary>Notes under the vault's templates folder (none when it is not configured).</summary>
public sealed record ListTemplates(VaultId VaultId) : IQuery<IReadOnlyList<VaultPath>>, IVaultRequest;

public sealed class ListTemplatesHandler(IVaultSessions sessions) : IQueryHandler<ListTemplates, IReadOnlyList<VaultPath>>
{
    public async Task<Result<IReadOnlyList<VaultPath>>> HandleAsync(ListTemplates query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        if (string.IsNullOrWhiteSpace(session.Value.Settings.TemplatesFolder) || VaultPath.Create(session.Value.Settings.TemplatesFolder) is not { IsSuccess: true } folder)
        {
            return Result.Success<IReadOnlyList<VaultPath>>([]);
        }

        var templates = new List<VaultPath>();
        await foreach (var entry in session.Value.FileSystem.EnumerateAsync(folder.Value, recursive: true, ct).ConfigureAwait(false))
        {
            if (!entry.IsDirectory && FileClassifier.Classify(entry.Path) == FileKind.Note)
            {
                templates.Add(entry.Path);
            }
        }

        return Result.Success<IReadOnlyList<VaultPath>>([.. templates.OrderBy(p => p.ToString(), StringComparer.Ordinal)]);
    }
}

/// <summary>The editor text after inserting a template, and where the cursor goes.</summary>
public sealed record TemplateInsertion(string Text, int Cursor);

/// <summary>
/// Inserts a template into the text being edited (F-009.3): <c>{{title}}</c> is the target note's title, the body goes
/// at <paramref name="Cursor"/>, and the template's frontmatter properties are added to the note's (the note's values
/// win). Nothing is written: the editor applies the result to its buffer and saves as usual.
/// </summary>
/// <returns><c>entry.not_found</c> when the template does not exist.</returns>
public sealed record InsertTemplate(VaultId VaultId, VaultPath TemplatePath, VaultPath TargetPath, string CurrentText, int Cursor) : IQuery<TemplateInsertion>, IVaultRequest;

public sealed class InsertTemplateHandler(IVaultSessions sessions, IMarkdownAnalyzer analyzer, IFrontmatterEditor frontmatter, IClock clock, ICultureProvider culture)
    : IQueryHandler<InsertTemplate, TemplateInsertion>
{
    public async Task<Result<TemplateInsertion>> HandleAsync(InsertTemplate query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var now = TimeZoneInfo.ConvertTime(clock.UtcNow, clock.LocalZone).DateTime;
        var rendered = await TemplateFiles.RenderAsync(session.Value.FileSystem, query.TemplatePath.ToString(), query.TargetPath.NameWithoutExtension, now, culture.Current, ct).ConfigureAwait(false);
        if (rendered.IsFailure)
        {
            return rendered.Error;
        }

        var templateFrontmatter = analyzer.Analyze(rendered.Value).Frontmatter;
        var body = templateFrontmatter is { IsValid: true } fm
            ? rendered.Value[(fm.Span.Start + fm.Span.Length)..].TrimStart('\r', '\n')
            : rendered.Value;

        var cursor = Math.Clamp(query.Cursor, 0, query.CurrentText.Length);
        var text = string.Concat(query.CurrentText.AsSpan(0, cursor), body, query.CurrentText.AsSpan(cursor));
        cursor += body.Length;

        // An invalid note frontmatter is left alone (the user fixes it in the source editor, F-010.2).
        var noteFrontmatter = analyzer.Analyze(text).Frontmatter;
        if (templateFrontmatter is { IsValid: true } && noteFrontmatter is not { IsValid: false })
        {
            var existing = noteFrontmatter?.Values.Entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var (key, value) in templateFrontmatter.Values.Entries.Where(e => !existing.Contains(e.Key)))
            {
                // Properties live at the top of the note: everything after them, including the cursor, shifts.
                var merged = frontmatter.SetProperty(text, key, value);
                cursor += merged.Length - text.Length;
                text = merged;
            }
        }

        return new TemplateInsertion(text, cursor);
    }
}

internal static class TemplateFiles
{
    public static async Task<Result<string>> RenderAsync(IVaultFileSystem fs, string templatePath, string title, DateTime now, System.Globalization.CultureInfo culture, CancellationToken ct)
    {
        var path = VaultPath.Create(templatePath);
        if (path.IsFailure)
        {
            return path.Error;
        }

        var file = await NoteFiles.ReadAsync(fs, path.Value, ct).ConfigureAwait(false);
        return file.Map(f => TemplateRenderer.Render(f.Content.Text, new TemplateContext(title, now, culture)));
    }
}
