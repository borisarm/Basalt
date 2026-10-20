using Basalt.Application.Bookmarks;
using Basalt.Application.Errors;
using Basalt.Application.Notes;
using Basalt.Application.Templates;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Commands;

public enum CommandPlatform
{
    Windows,
    MacOS,
}

[Flags]
public enum GestureModifiers
{
    None = 0,
    Control = 1,

    /// <summary>Cmd on macOS, Win on Windows.</summary>
    Meta = 2,
    Alt = 4,
    Shift = 8,
}

/// <summary>A platform-neutral key gesture (spec 07 §9). <c>Mod</c> means Ctrl on Windows and Cmd on macOS.</summary>
public sealed record Gesture(GestureModifiers Modifiers, string Key)
{
    /// <summary>Parses <c>Mod+Shift+T</c>, <c>Ctrl+E</c>, <c>Cmd+[</c>, <c>F2</c>… (case-insensitive; modifiers in any order).</summary>
    /// <returns><c>gesture.invalid</c>.</returns>
    public static Result<Gesture> Parse(string text, CommandPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = GestureModifiers.None;
        foreach (var part in parts[..^1])
        {
            if (Modifier(part, platform) is not { } modifier)
            {
                return Invalid(text);
            }

            modifiers |= modifier;
        }

        var key = parts[^1];
        if (key.Length == 0 || Modifier(key, platform) is not null)
        {
            return Invalid(text);
        }

        return new Gesture(modifiers, key.Length == 1 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key[1..]);
    }

    public string ToString(CommandPlatform platform)
    {
        var names = new List<string>();
        if (Modifiers.HasFlag(GestureModifiers.Control))
        {
            names.Add("Ctrl");
        }

        if (Modifiers.HasFlag(GestureModifiers.Meta))
        {
            names.Add(platform == CommandPlatform.MacOS ? "Cmd" : "Win");
        }

        if (Modifiers.HasFlag(GestureModifiers.Alt))
        {
            names.Add("Alt");
        }

        if (Modifiers.HasFlag(GestureModifiers.Shift))
        {
            names.Add("Shift");
        }

        names.Add(Key);
        return string.Join('+', names);
    }

    private static GestureModifiers? Modifier(string name, CommandPlatform platform) => name.ToUpperInvariant() switch
    {
        "MOD" => platform == CommandPlatform.MacOS ? GestureModifiers.Meta : GestureModifiers.Control,
        "CTRL" or "CONTROL" => GestureModifiers.Control,
        "CMD" or "COMMAND" or "META" or "WIN" or "SUPER" => GestureModifiers.Meta,
        "ALT" or "OPTION" => GestureModifiers.Alt,
        "SHIFT" => GestureModifiers.Shift,
        _ => null,
    };

    private static Error Invalid(string text) => new Error(ErrorCodes.GestureInvalid, "The key gesture is not valid.").With("gesture", text);
}

/// <summary>What a command runs on: the active vault and file, and the UI host.</summary>
public sealed record CommandContext(VaultId? Vault, VaultPath? ActiveFile, ICommandHost Host);

/// <summary>What commands may ask of the UI (implemented by Presentation).</summary>
public interface ICommandHost
{
    Task OpenAsync(VaultPath path, bool newTab, CancellationToken ct);
}

/// <summary>A command (spec 07 §9): <see cref="DefaultGesture"/> uses <c>Mod</c> notation, e.g. <c>Mod+Shift+T</c>.</summary>
public sealed record CommandDescriptor(
    string Id,
    string TitleKey,
    string Category,
    string? DefaultGesture,
    Func<CommandContext, bool> CanExecute,
    Func<CommandContext, CancellationToken, Task<Result<Unit>>> ExecuteAsync);

/// <summary>A module's commands (Open/Closed: modules add commands without touching the registry).</summary>
public interface ICommandContribution
{
    IEnumerable<CommandDescriptor> GetCommands();
}

public interface ICommandRegistry
{
    IReadOnlyList<CommandDescriptor> Commands { get; }

    CommandDescriptor? Find(string id);

    /// <summary>The command whose default gesture is <paramref name="gesture"/> on <paramref name="platform"/>.</summary>
    CommandDescriptor? FindByGesture(Gesture gesture, CommandPlatform platform);

    /// <returns><c>command.not_found</c>, <c>command.unavailable</c> or the command's own error.</returns>
    Task<Result<Unit>> ExecuteAsync(string id, CommandContext context, CancellationToken ct);
}

/// <summary>All contributed commands. Duplicate ids are a programming error and fail at construction.</summary>
public sealed class CommandRegistry : ICommandRegistry
{
    private readonly Dictionary<string, CommandDescriptor> _byId = new(StringComparer.Ordinal);

    public CommandRegistry(IEnumerable<ICommandContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        var commands = new List<CommandDescriptor>();
        foreach (var command in contributions.SelectMany(c => c.GetCommands()))
        {
            if (!_byId.TryAdd(command.Id, command))
            {
                throw new InvalidOperationException($"The command '{command.Id}' is registered twice.");
            }

            commands.Add(command);
        }

        Commands = commands;
    }

    public IReadOnlyList<CommandDescriptor> Commands { get; }

    public CommandDescriptor? Find(string id) => _byId.GetValueOrDefault(id);

    public CommandDescriptor? FindByGesture(Gesture gesture, CommandPlatform platform) =>
        Commands.FirstOrDefault(c => c.DefaultGesture is { } text && Gesture.Parse(text, platform) is { IsSuccess: true } parsed && parsed.Value == gesture);

    public async Task<Result<Unit>> ExecuteAsync(string id, CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Find(id) is not { } command)
        {
            return new Error(ErrorCodes.CommandNotFound, "There is no such command.").With("command", id);
        }

        return command.CanExecute(context)
            ? await command.ExecuteAsync(context, ct).ConfigureAwait(false)
            : new Error(ErrorCodes.CommandUnavailable, "The command cannot run now.").With("command", id);
    }
}

/// <summary>Application-level commands; commands acting on views are contributed by Presentation.</summary>
public sealed class CoreCommands(CreateNoteHandler createNote, OpenDailyNoteHandler dailyNote, AddBookmarkHandler addBookmark) : ICommandContribution
{
    public const string NewNote = "file.new-note";
    public const string OpenDailyNote = "daily-notes.open-today";
    public const string BookmarkActiveFile = "bookmarks.add-active-file";

    public IEnumerable<CommandDescriptor> GetCommands() =>
    [
        new(NewNote, "Commands.NewNote", "file", "Mod+N", HasVault, async (context, ct) =>
        {
            var created = await createNote.HandleAsync(new CreateNote(context.Vault!.Value), ct).ConfigureAwait(false);
            return await OpenAsync(context, created, ct).ConfigureAwait(false);
        }),
        new(OpenDailyNote, "Commands.OpenDailyNote", "daily-notes", null, HasVault, async (context, ct) =>
        {
            var note = await dailyNote.HandleAsync(new Templates.OpenDailyNote(context.Vault!.Value), ct).ConfigureAwait(false);
            return await OpenAsync(context, note.Map(n => n.Path), ct).ConfigureAwait(false);
        }),
        new(BookmarkActiveFile, "Commands.BookmarkActiveFile", "bookmarks", null, c => HasVault(c) && c.ActiveFile is not null, (context, ct) =>
            addBookmark.HandleAsync(new AddBookmark(context.Vault!.Value, Bookmark.ForFile(context.ActiveFile!.Value.ToString())), ct)),
    ];

    private static bool HasVault(CommandContext context) => context.Vault is not null;

    private static async Task<Result<Unit>> OpenAsync(CommandContext context, Result<VaultPath> path, CancellationToken ct)
    {
        if (path.IsFailure)
        {
            return path.Error;
        }

        await context.Host.OpenAsync(path.Value, newTab: false, ct).ConfigureAwait(false);
        return Result.Success();
    }
}
