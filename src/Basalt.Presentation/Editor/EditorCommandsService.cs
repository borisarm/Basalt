using System.Globalization;
using System.Text.RegularExpressions;
using Basalt.Domain.Links;

namespace Basalt.Presentation.Editor;

/// <summary>The editor buffer as the commands see it.</summary>
public readonly record struct TextBufferState(string Text, int SelectionStart, int SelectionLength)
{
    public int SelectionEnd => SelectionStart + SelectionLength;

    /// <summary>Applies the edits (offsets of this text, applied back to front) and takes the result's selection.</summary>
    public TextBufferState Apply(EditResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = Text;
        foreach (var edit in result.Edits.Select((e, i) => (Edit: e, Index: i)).OrderByDescending(x => x.Edit.Offset).ThenByDescending(x => x.Index))
        {
            text = text.Remove(edit.Edit.Offset, edit.Edit.Length).Insert(edit.Edit.Offset, edit.Edit.Replacement);
        }

        return new TextBufferState(text, result.SelectionStart, result.SelectionLength);
    }
}

/// <summary>Replace <see cref="Length"/> characters at <see cref="Offset"/> (offsets of the original text).</summary>
public readonly record struct TextEdit(int Offset, int Length, string Replacement);

/// <summary>Edits to apply and the selection afterwards (in the edited text).</summary>
public sealed record EditResult(IReadOnlyList<TextEdit> Edits, int SelectionStart, int SelectionLength);

/// <summary>Editor behaviours (spec 07 §5.2); null means "not handled, do the default".</summary>
public interface IEditorCommands
{
    EditResult? Enter(TextBufferState state);

    EditResult? Indent(TextBufferState state);

    EditResult? Outdent(TextBufferState state);

    EditResult ToggleWrap(TextBufferState state, string marker);

    EditResult ToggleTask(TextBufferState state);

    EditResult InsertLink(TextBufferState state);

    EditResult? AutoClose(TextBufferState state, char typed);
}

/// <summary>
/// Editor behaviours (spec 07 §5.2) as pure functions over a <see cref="TextBufferState"/>; null means "not handled,
/// do the default". The indentation unit for lists is a tab; outdenting also removes up to four spaces.
/// </summary>
public sealed partial class EditorCommandsService : IEditorCommands
{
    private const string IndentUnit = "\t";

    /// <summary>Enter: continues lists, tasks and quotes; on an empty item removes its marker (F-003.4).</summary>
    public EditResult? Enter(TextBufferState state)
    {
        var text = state.Text.Remove(state.SelectionStart, state.SelectionLength);
        var caret = state.SelectionStart;
        if (CodeRegions.Find(text).Any(r => r.Start < caret && caret <= r.End))
        {
            return null;
        }

        var (lineStart, lineEnd) = LineAt(text, caret);
        if (Prefix(text, lineStart, lineEnd) is not { } prefix || caret < lineStart + prefix.Length)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text[(lineStart + prefix.Length)..lineEnd]))
        {
            return new EditResult([new TextEdit(lineStart, prefix.Length, string.Empty), new TextEdit(state.SelectionStart, state.SelectionLength, string.Empty)], caret - prefix.Length, 0);
        }

        var continuation = "\n" + prefix.Continuation;
        return new EditResult([new TextEdit(state.SelectionStart, state.SelectionLength, continuation)], caret + continuation.Length, 0);
    }

    /// <summary>Tab on list items: indents every list line touched by the selection.</summary>
    public EditResult? Indent(TextBufferState state)
    {
        var lines = ListLines(state);
        if (lines.Count == 0)
        {
            return null;
        }

        var edits = lines.Select(l => new TextEdit(l.Start, 0, IndentUnit)).ToList();
        if (state.SelectionLength == 0)
        {
            return new EditResult(edits, state.SelectionStart + IndentUnit.Length, 0);
        }

        var shiftStart = lines[0].Start < state.SelectionStart ? IndentUnit.Length : 0;
        return new EditResult(edits, state.SelectionStart + shiftStart, state.SelectionLength + (edits.Count * IndentUnit.Length) - shiftStart);
    }

    /// <summary>Shift+Tab on list items: removes one tab or up to four spaces of indentation.</summary>
    public EditResult? Outdent(TextBufferState state)
    {
        var edits = new List<TextEdit>();
        foreach (var (start, _) in ListLines(state))
        {
            var text = state.Text;
            var remove = start < text.Length && text[start] == '\t' ? 1 : 0;
            if (remove == 0)
            {
                while (remove < 4 && start + remove < text.Length && text[start + remove] == ' ')
                {
                    remove++;
                }
            }

            if (remove > 0)
            {
                edits.Add(new TextEdit(start, remove, string.Empty));
            }
        }

        if (edits.Count == 0)
        {
            return null;
        }

        int Map(int position) => position - edits.Sum(e => Math.Clamp(position - e.Offset, 0, e.Length));
        var newStart = Map(state.SelectionStart);
        return new EditResult(edits, newStart, Map(state.SelectionEnd) - newStart);
    }

    /// <summary>Bold (<c>**</c>), italic (<c>*</c>), code, strikethrough (<c>~~</c>), highlight (<c>==</c>) on the selection or at the caret.</summary>
    public EditResult ToggleWrap(TextBufferState state, string marker)
    {
        ArgumentException.ThrowIfNullOrEmpty(marker);
        var (text, start, end) = (state.Text, state.SelectionStart, state.SelectionEnd);
        var length = marker.Length;
        if (state.SelectionLength >= 2 * length)
        {
            var selected = text[start..end];
            if (Present(RunAt(selected, 0, marker[0], +1), marker) && Present(RunAt(selected, selected.Length - 1, marker[0], -1), marker))
            {
                return new EditResult([new TextEdit(start, state.SelectionLength, selected[length..^length])], start, state.SelectionLength - (2 * length));
            }
        }

        if (Present(RunAt(text, start - 1, marker[0], -1), marker) && Present(RunAt(text, end, marker[0], +1), marker))
        {
            return new EditResult([new TextEdit(start - length, length, string.Empty), new TextEdit(end, length, string.Empty)], start - length, state.SelectionLength);
        }

        return new EditResult([new TextEdit(start, 0, marker), new TextEdit(end, 0, marker)], start + length, state.SelectionLength);
    }

    /// <summary>Turns the current line into a task, or toggles its checkbox.</summary>
    public EditResult ToggleTask(TextBufferState state)
    {
        var caret = state.SelectionStart;
        var (lineStart, lineEnd) = LineAt(state.Text, caret);
        var line = state.Text[lineStart..lineEnd];
        var match = ListPrefix().Match(line);
        if (match.Success && match.Groups["task"].Success)
        {
            var status = lineStart + match.Groups["task"].Index + 1;
            return new EditResult([new TextEdit(status, 1, state.Text[status] == ' ' ? "x" : " ")], state.SelectionStart, state.SelectionLength);
        }

        var (offset, insert) = match.Success
            ? (lineStart + match.Length, "[ ] ")
            : (lineStart + Indentation(line), "- [ ] ");
        var shift = caret >= offset ? insert.Length : 0;
        return new EditResult([new TextEdit(offset, 0, insert)], caret + shift, state.SelectionLength);
    }

    /// <summary>Ctrl+K: a selected URL becomes <c>[](url)</c>; anything else <c>[[selection]]</c>.</summary>
    public EditResult InsertLink(TextBufferState state)
    {
        var selected = state.Text.Substring(state.SelectionStart, state.SelectionLength);
        if (IsUrl(selected))
        {
            return new EditResult([new TextEdit(state.SelectionStart, state.SelectionLength, $"[]({selected})")], state.SelectionStart + 1, 0);
        }

        return new EditResult([new TextEdit(state.SelectionStart, state.SelectionLength, $"[[{selected}]]")], state.SelectionStart + 2 + selected.Length, 0);
    }

    /// <summary>
    /// Auto-closing of <c>(</c>, <c>`</c>, <c>[[</c> and <c>**</c>, typing over the closing character, and wrapping a
    /// selection with <c>(</c>, <c>`</c> or <c>*</c>.
    /// </summary>
    public EditResult? AutoClose(TextBufferState state, char typed)
    {
        var (text, caret) = (state.Text, state.SelectionStart);
        if (state.SelectionLength > 0)
        {
            return typed switch
            {
                '(' => Wrap(state, "(", ")"),
                '`' => Wrap(state, "`", "`"),
                '*' => Wrap(state, "*", "*"),
                _ => null,
            };
        }

        char? previous = caret > 0 ? text[caret - 1] : null;
        char? next = caret < text.Length ? text[caret] : null;
        var nextIsFree = next is null || char.IsWhiteSpace(next.Value) || next is ')' or ']';
        return typed switch
        {
            ')' or ']' when next == typed => new EditResult([], caret + 1, 0),
            '`' when next == '`' => new EditResult([], caret + 1, 0),
            '(' or '`' when nextIsFree => new EditResult([new TextEdit(caret, 0, typed == '(' ? "()" : "``")], caret + 1, 0),
            '[' when previous == '[' => new EditResult([new TextEdit(caret, 0, next == ']' ? "[]" : "[]]")], caret + 1, 0),
            '*' when previous == '*' => new EditResult([new TextEdit(caret, 0, "***")], caret + 1, 0),
            _ => null,
        };
    }

    private static EditResult Wrap(TextBufferState state, string open, string close) =>
        new([new TextEdit(state.SelectionStart, 0, open), new TextEdit(state.SelectionEnd, 0, close)], state.SelectionStart + open.Length, state.SelectionLength);

    /// <summary>Whether a run of <paramref name="run"/> marker characters contains the marker (<c>***</c> holds both <c>*</c> and <c>**</c>).</summary>
    private static bool Present(int run, string marker) => marker == "*" ? run is 1 or 3 : marker == "**" ? run >= 2 : run >= marker.Length;

    private static int RunAt(string text, int from, char c, int step)
    {
        var count = 0;
        for (var i = from; i >= 0 && i < text.Length && text[i] == c; i += step)
        {
            count++;
        }

        return count;
    }

    private static bool IsUrl(string text) =>
        (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        && !text.Any(char.IsWhiteSpace);

    private static (int Start, int End) LineAt(string text, int position)
    {
        var start = position == 0 ? 0 : text.LastIndexOf('\n', position - 1) + 1;
        var newline = text.IndexOf('\n', position);
        return (start, newline < 0 ? text.Length : newline);
    }

    /// <summary>The list lines touched by the selection (or the caret's line).</summary>
    private static List<(int Start, int End)> ListLines(TextBufferState state)
    {
        var lines = new List<(int Start, int End)>();
        var (start, end) = LineAt(state.Text, state.SelectionStart);
        while (true)
        {
            if (ListPrefix().IsMatch(state.Text[start..end]))
            {
                lines.Add((start, end));
            }

            if (end >= state.SelectionEnd || end >= state.Text.Length)
            {
                return lines;
            }

            (start, end) = LineAt(state.Text, end + 1);
        }
    }

    private static int Indentation(string line) => line.Length - line.TrimStart(' ', '\t').Length;

    private static LinePrefix? Prefix(string text, int lineStart, int lineEnd)
    {
        var line = text[lineStart..lineEnd];
        var list = ListPrefix().Match(line);
        if (list.Success)
        {
            var marker = list.Groups["marker"].Value;
            var next = char.IsAsciiDigit(marker[0])
                ? (long.Parse(marker[..^1], CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture) + marker[^1]
                : marker;
            var task = list.Groups["task"].Success ? "[ ] " : string.Empty;
            return new LinePrefix(list.Length, list.Groups["indent"].Value + next + list.Groups["space"].Value + task);
        }

        var quote = QuotePrefix().Match(line);
        return quote.Success ? new LinePrefix(quote.Length, quote.Value) : null;
    }

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<marker>[-*+]|\d{1,9}[.)])(?<space>[ \t]+)(?<task>\[[ xX]\][ \t]+)?")]
    private static partial Regex ListPrefix();

    [GeneratedRegex(@"^[ \t]*> ?")]
    private static partial Regex QuotePrefix();

    private sealed record LinePrefix(int Length, string Continuation);
}
