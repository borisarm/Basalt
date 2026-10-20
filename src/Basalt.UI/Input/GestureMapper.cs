using Avalonia.Input;
using Basalt.Application.Commands;

namespace Basalt.UI.Input;

/// <summary>Translates Avalonia key events into the neutral <see cref="Gesture"/> of Presentation (spec 07 §9).</summary>
public static class GestureMapper
{
    /// <summary>The gesture of a key press, or null for keys that never start a shortcut (bare modifiers, unknown keys).</summary>
    public static Gesture? From(Key key, KeyModifiers modifiers)
    {
        if (KeyName(key) is not { } name)
        {
            return null;
        }

        var neutral = GestureModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            neutral |= GestureModifiers.Control;
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            neutral |= GestureModifiers.Meta;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            neutral |= GestureModifiers.Alt;
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            neutral |= GestureModifiers.Shift;
        }

        return new Gesture(neutral, name);
    }

    private static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Left or Key.Right or Key.Up or Key.Down or Key.Enter or Key.Tab or Key.Space or Key.Escape or Key.Delete or Key.Home or Key.End
            or Key.PageUp or Key.PageDown or Key.Insert => key.ToString(),
        Key.Back => "Backspace",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemMinus or Key.Subtract => "-",
        Key.OemPlus or Key.Add => "=",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemPipe or Key.OemBackslash => "\\",
        Key.OemTilde => "`",
        _ => null,
    };
}
