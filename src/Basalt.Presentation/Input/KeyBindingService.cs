using Basalt.Application.Commands;
using Basalt.Application.Errors;
using Basalt.Domain.Common;

namespace Basalt.Presentation.Input;

/// <summary>Several commands answer to the same gesture.</summary>
public sealed record KeyBindingConflict(string Gesture, IReadOnlyList<string> CommandIds);

/// <summary>
/// Gesture ↔ command resolution (spec 07 §9): defaults from the command registry, user overrides (<c>AppSettings.Hotkeys</c>,
/// command id → gesture; empty = unassigned) win and resolve conflicts.
/// </summary>
public sealed class KeyBindingService(ICommandRegistry registry, CommandPlatform platform)
{
    private Dictionary<string, string> _overrides = new(StringComparer.Ordinal);

    public CommandPlatform Platform => platform;

    public IReadOnlyDictionary<string, string> Overrides => _overrides;

    /// <summary>Commands sharing an effective gesture.</summary>
    public IReadOnlyList<KeyBindingConflict> Conflicts =>
    [
        .. registry.Commands
            .Select(c => (c.Id, Gesture: GestureFor(c.Id)))
            .Where(x => x.Gesture is not null)
            .GroupBy(x => x.Gesture!)
            .Where(g => g.Count() > 1)
            .Select(g => new KeyBindingConflict(g.Key.ToString(platform), [.. g.Select(x => x.Id)])),
    ];

    public void ApplyOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        _overrides = new Dictionary<string, string>(overrides, StringComparer.Ordinal);
    }

    /// <summary>The effective gesture of a command, or null when it has none.</summary>
    public Gesture? GestureFor(string commandId)
    {
        var isCustom = _overrides.TryGetValue(commandId, out var custom);
        var text = isCustom ? custom : registry.Find(commandId)?.DefaultGesture;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // User gestures are a single platform's gesture (and may use ';' as a key); defaults may hold "windows;macos".
        var parsed = isCustom ? Gesture.Parse(text, platform) : Gesture.ParseDefault(text, platform);
        return parsed.IsSuccess ? parsed.Value : null;
    }

    /// <summary>The command for a key gesture; with a conflict, a user-assigned command wins.</summary>
    public CommandDescriptor? Resolve(Gesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        var matches = registry.Commands.Where(c => GestureFor(c.Id) == gesture).ToList();
        return matches.FirstOrDefault(c => _overrides.ContainsKey(c.Id)) ?? matches.FirstOrDefault();
    }

    /// <summary>Assigns a gesture (null resets to the default, empty unassigns) and returns the overrides to persist.</summary>
    /// <returns><c>command.not_found</c>, <c>gesture.invalid</c>.</returns>
    public Result<IReadOnlyDictionary<string, string>> Reassign(string commandId, string? gesture)
    {
        if (registry.Find(commandId) is null)
        {
            return new Error(ErrorCodes.CommandNotFound, "There is no such command.").With("command", commandId);
        }

        var updated = new Dictionary<string, string>(_overrides, StringComparer.Ordinal);
        if (gesture is null)
        {
            updated.Remove(commandId);
        }
        else if (gesture.Length == 0)
        {
            updated[commandId] = string.Empty;
        }
        else
        {
            var parsed = Gesture.Parse(gesture, platform);
            if (parsed.IsFailure)
            {
                return parsed.Error;
            }

            updated[commandId] = parsed.Value.ToString(platform);
        }

        _overrides = updated;
        return updated;
    }
}
