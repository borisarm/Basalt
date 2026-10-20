namespace Basalt.Presentation.Editor;

public enum CompletionKind
{
    Link,
    Tag,
}

/// <summary>What is being completed: the text typed so far (<see cref="Partial"/>) starts at <see cref="Start"/>.</summary>
public sealed record CompletionContext(CompletionKind Kind, string Partial, int Start);

/// <summary>An entry of the completion list; <see cref="Source"/> is the suggestion it comes from.</summary>
public sealed record CompletionItem(string Display, string? Detail, CompletionKind Kind, object Source);

/// <summary>Finds whether the caret is completing a link (<c>[[…</c>) or a tag (<c>#…</c>), spec 07 §5.1.</summary>
public static class CompletionTrigger
{
    /// <summary>
    /// Link: an open <c>[[</c> on the caret's line with no <c>]]</c> or alias <c>|</c> after it. Tag: <c>#</c> after a
    /// space, punctuation or the line start, followed by at least one tag character.
    /// </summary>
    public static CompletionContext? Detect(string text, int caret)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var line = text[lineStart..caret];
        var open = line.LastIndexOf("[[", StringComparison.Ordinal);
        if (open >= 0)
        {
            var partial = line[(open + 2)..];
            return partial.Contains("]]", StringComparison.Ordinal) || partial.Contains('|', StringComparison.Ordinal)
                ? null
                : new CompletionContext(CompletionKind.Link, partial, lineStart + open + 2);
        }

        var start = caret;
        while (start > lineStart && IsTagCharacter(text[start - 1]))
        {
            start--;
        }

        var hash = start - 1;
        var precededByWord = hash > 0 && char.IsLetterOrDigit(text[hash - 1]);
        return start < caret && hash >= lineStart && text[hash] == '#' && !precededByWord
            ? new CompletionContext(CompletionKind.Tag, text[start..caret], start)
            : null;
    }

    private static bool IsTagCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '/';
}
