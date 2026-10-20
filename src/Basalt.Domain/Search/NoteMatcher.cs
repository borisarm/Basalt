using System.Text.RegularExpressions;
using Basalt.Domain.Common;
using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;

namespace Basalt.Domain.Search;

/// <summary>Where a <see cref="MatchRange"/> lies.</summary>
public enum MatchTarget
{
    Content,
    FileName,
    Path,
}

/// <summary>A highlighted range: UTF-16 offsets into the note text, its file name or its path.</summary>
public readonly record struct MatchRange(MatchTarget Target, int Start, int Length);

/// <summary>A positive match and the ranges to highlight (ordered by target, then start).</summary>
public sealed record SearchMatch(IReadOnlyList<MatchRange> Ranges);

/// <summary>What the matcher sees of a vault file (attachments have empty text and metadata).</summary>
public sealed record SearchableNote(VaultPath Path, string Text, NoteMetadata Metadata);

/// <summary>Evaluates a query exactly against one note (spec 08 §4.3 step 3).</summary>
public interface INoteMatcher
{
    /// <returns>Null when the note does not match.</returns>
    SearchMatch? Match(SearchableNote note);
}

/// <summary>
/// The exact semantics of spec 08 §4.2. Terms and phrases are case- and diacritic-insensitive substring matches
/// (ordinal with <c>matchCase</c>, which also makes regexes case-sensitive; tags and properties stay insensitive);
/// regexes run on the original text (case-insensitive, NonBacktracking, 200 ms per evaluation, a timeout counts as no
/// match). Scoped operators (<c>line:</c>, <c>block:</c>, <c>section:</c>, <c>task*:</c>) require one unit that
/// satisfies the whole operand. Negated parts never contribute highlight ranges.
/// </summary>
public static class NoteMatcher
{
    public static INoteMatcher Create(SearchQuery query, bool matchCase = false)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new Matcher(query.Root, matchCase);
    }

    private sealed class Matcher(SearchNode? root, bool matchCase) : INoteMatcher
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

        private readonly Dictionary<string, Regex> _regexes = [];
        private readonly Dictionary<string, string> _folded = [];

        public SearchMatch? Match(SearchableNote note)
        {
            ArgumentNullException.ThrowIfNull(note);
            if (root is null)
            {
                return null;
            }

            var context = new NoteContext(note);
            var ranges = new List<MatchRange>();
            if (!Evaluate(root, context, context.DefaultScope, ranges))
            {
                return null;
            }

            return new SearchMatch([.. ranges.Distinct().OrderBy(r => r.Target).ThenBy(r => r.Start)]);
        }

        private bool Evaluate(SearchNode node, NoteContext context, Scope scope, List<MatchRange>? ranges) => node switch
        {
            TermNode term => MatchText(term.Text, scope, ranges),
            PhraseNode phrase => MatchText(phrase.Text, scope, ranges),
            RegexNode regex => MatchRegex(regex.Pattern, scope, ranges),
            AndNode and => EvaluateAnd(and, context, scope, ranges),
            OrNode or => EvaluateOr(or, context, scope, ranges),
            NotNode not => !Evaluate(not.Operand, context, scope, null),
            GroupNode group => Evaluate(group.Inner, context, scope, ranges),
            FieldNode field => EvaluateField(field, context, ranges),
            PropertyNode property => MatchProperty(property, context.Note.Metadata.Frontmatter),
            _ => throw new ArgumentOutOfRangeException(nameof(node)),
        };

        private bool EvaluateAnd(AndNode and, NoteContext context, Scope scope, List<MatchRange>? ranges)
        {
            var collected = ranges is null ? null : new List<MatchRange>();
            foreach (var operand in and.Operands)
            {
                if (!Evaluate(operand, context, scope, collected))
                {
                    return false;
                }
            }

            ranges?.AddRange(collected!);
            return true;
        }

        private bool EvaluateOr(OrNode or, NoteContext context, Scope scope, List<MatchRange>? ranges)
        {
            var any = false;
            foreach (var operand in or.Operands)
            {
                any |= Evaluate(operand, context, scope, ranges);
            }

            return any;
        }

        private bool EvaluateField(FieldNode field, NoteContext context, List<MatchRange>? ranges) => field.Field switch
        {
            SearchField.File => Evaluate(field.Operand, context, context.FileNameScope, ranges),
            SearchField.Path => Evaluate(field.Operand, context, context.PathScope, ranges),
            SearchField.Content => Evaluate(field.Operand, context, context.ContentScope, ranges),
            SearchField.Tag => Evaluate(field.Operand, context, context.TagScope, ranges),
            SearchField.Line => Any(field.Operand, context, context.Lines, ranges),
            SearchField.Block => Any(field.Operand, context, context.Blocks, ranges),
            SearchField.Section => Any(field.Operand, context, context.Sections, ranges),
            SearchField.Task => Any(field.Operand, context, context.Tasks(_ => true), ranges),
            SearchField.TaskTodo => Any(field.Operand, context, context.Tasks(t => !t.IsDone), ranges),
            SearchField.TaskDone => Any(field.Operand, context, context.Tasks(t => t.IsDone), ranges),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        /// <summary>True when at least one unit satisfies the operand on its own; ranges come from every such unit.</summary>
        private bool Any(SearchNode operand, NoteContext context, IEnumerable<Segment> units, List<MatchRange>? ranges)
        {
            var any = false;
            foreach (var unit in units)
            {
                if (Evaluate(operand, context, Scope.Of(unit), ranges))
                {
                    any = true;
                    if (ranges is null)
                    {
                        break;
                    }
                }
            }

            return any;
        }

        private bool MatchText(string text, Scope scope, List<MatchRange>? ranges)
        {
            if (scope.Tags is { } tags)
            {
                return MatchTags(Fold(text).TrimStart('#'), tags, ranges);
            }

            var needle = matchCase ? text : Fold(text);

            if (needle.Length == 0)
            {
                return true;
            }

            var any = false;
            foreach (var segment in scope.Segments)
            {
                foreach (var (start, length) in matchCase ? FindExact(segment.Text, needle) : segment.Folded.FindAll(needle))
                {
                    any = true;
                    if (ranges is null)
                    {
                        return true;
                    }

                    ranges.Add(new MatchRange(segment.Target, segment.Offset + start, length));
                }
            }

            return any;
        }

        private static IEnumerable<(int Start, int Length)> FindExact(string text, string needle)
        {
            for (var index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
            {
                yield return (index, needle.Length);
            }
        }

        private static bool MatchTags(string needle, IReadOnlyList<TagUnit> tags, List<MatchRange>? ranges)
        {
            var any = false;
            foreach (var tag in tags)
            {
                if (tag.Name != needle && !tag.Name.StartsWith(needle + "/", StringComparison.Ordinal))
                {
                    continue;
                }

                any = true;
                if (ranges is null)
                {
                    return true;
                }

                if (tag.Line is { } line && line.Folded.FindAll("#" + tag.Name).FirstOrDefault() is { Length: > 0 } found)
                {
                    ranges.Add(new MatchRange(MatchTarget.Content, line.Offset + found.Start, found.Length));
                }
            }

            return any;
        }

        private bool MatchRegex(string pattern, Scope scope, List<MatchRange>? ranges)
        {
            var regex = RegexFor(pattern);
            var texts = scope.Tags is { } tags
                ? tags.Select(t => new Segment(MatchTarget.Content, t.Name, -1))
                : scope.Segments;
            var any = false;
            foreach (var segment in texts)
            {
                try
                {
                    for (var match = regex.Match(segment.Text); match.Success; match = match.NextMatch())
                    {
                        any = true;
                        if (ranges is null)
                        {
                            return true;
                        }

                        if (segment.Offset >= 0 && match.Length > 0)
                        {
                            ranges.Add(new MatchRange(segment.Target, segment.Offset + match.Index, match.Length));
                        }
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // NFR-032: a pathological pattern counts as no further match in this text.
                }
            }

            return any;
        }

        private static bool MatchProperty(PropertyNode property, Frontmatter? frontmatter)
        {
            var entry = frontmatter?.Values.Entries.FirstOrDefault(e => string.Equals(e.Key, property.Key, StringComparison.OrdinalIgnoreCase));
            if (entry?.Key is null)
            {
                return false;
            }

            var value = entry.Value.Value;
            return property.Test switch
            {
                PropertyTest.Exists => true,
                PropertyTest.IsEmpty => IsEmpty(value),
                _ => Scalars(value).Any(s => FoldedText.Fold(s).Contains(FoldedText.Fold(property.Value ?? string.Empty), StringComparison.Ordinal)),
            };
        }

        private static bool IsEmpty(StructuredValue value) => value switch
        {
            StructuredValue.NullValue => true,
            StructuredValue.TextValue text => text.Value.Length == 0,
            StructuredValue.ListValue list => list.Items.Count == 0,
            StructuredValue.MapValue map => map.Entries.Count == 0,
            _ => false,
        };

        private static IEnumerable<string> Scalars(StructuredValue value) => value switch
        {
            StructuredValue.TextValue text => [text.Value],
            StructuredValue.NumberValue number => [number.Raw],
            StructuredValue.BoolValue boolean => [boolean.Value ? "true" : "false"],
            StructuredValue.DateValue date => [date.Raw],
            StructuredValue.DateTimeValue dateTime => [dateTime.Raw],
            StructuredValue.ListValue list => list.Items.SelectMany(Scalars),
            StructuredValue.MapValue map => map.Entries.SelectMany(e => Scalars(e.Value)),
            _ => [],
        };

        private string Fold(string text)
        {
            if (!_folded.TryGetValue(text, out var folded))
            {
                _folded[text] = folded = FoldedText.Fold(text);
            }

            return folded;
        }

        private Regex RegexFor(string pattern)
        {
            if (!_regexes.TryGetValue(pattern, out var regex))
            {
                var options = matchCase ? SearchQueryParser.RegexOptionsFor & ~RegexOptions.IgnoreCase : SearchQueryParser.RegexOptionsFor;
                _regexes[pattern] = regex = new Regex(pattern, options, RegexTimeout);
            }

            return regex;
        }
    }

    /// <summary>A piece of searchable text; <see cref="Offset"/> maps its positions into the target (−1: no ranges).</summary>
    private sealed class Segment(MatchTarget target, string text, int offset)
    {
        private FoldedText? _folded;

        public MatchTarget Target { get; } = target;

        public string Text { get; } = text;

        public int Offset { get; } = offset;

        public FoldedText Folded => _folded ??= FoldedText.Create(Text);
    }

    private sealed record TagUnit(string Name, Segment? Line);

    /// <summary>Where terms are looked for: text segments, or the note's tags for <c>tag:</c>.</summary>
    private sealed record Scope(IReadOnlyList<Segment> Segments, IReadOnlyList<TagUnit>? Tags = null)
    {
        public static Scope Of(Segment segment) => new([segment]);
    }

    /// <summary>Lazily computed views of one note.</summary>
    private sealed class NoteContext
    {
        private readonly Segment _content;
        private List<Segment>? _lines;
        private Scope? _tagScope;

        public NoteContext(SearchableNote note)
        {
            Note = note;
            _content = new Segment(MatchTarget.Content, note.Text, 0);
            var name = new Segment(MatchTarget.FileName, note.Path.Name, 0);
            ContentScope = Scope.Of(_content);
            FileNameScope = Scope.Of(name);
            PathScope = Scope.Of(new Segment(MatchTarget.Path, note.Path.ToString(), 0));
            DefaultScope = new Scope([_content, name]);
        }

        public SearchableNote Note { get; }

        public Scope DefaultScope { get; }

        public Scope ContentScope { get; }

        public Scope FileNameScope { get; }

        public Scope PathScope { get; }

        public Scope TagScope => _tagScope ??= new Scope([], [.. Note.Metadata.Tags.Select(t => new TagUnit(
            FoldedText.Fold(t.Tag.ToString().TrimStart('#')),
            t.Source == TagSource.Text && t.Line < Lines.Count ? Lines[t.Line] : null))]);

        public IReadOnlyList<Segment> Lines => _lines ??= SplitLines();

        public IEnumerable<Segment> Blocks => Group(Lines, StartsBlock, IsBlank);

        public IEnumerable<Segment> Sections
        {
            get
            {
                var headings = Note.Metadata.Headings.Select(h => h.Line).ToHashSet();
                return Group(Lines, (_, index) => headings.Contains(index), _ => false);
            }
        }

        public IEnumerable<Segment> Tasks(Func<TaskItem, bool> filter) =>
            Note.Metadata.Tasks.Where(filter).Where(t => t.Line < Lines.Count).Select(t => Lines[t.Line]);

        private List<Segment> SplitLines()
        {
            var lines = new List<Segment>();
            var text = Note.Text;
            var start = 0;
            while (start <= text.Length)
            {
                var end = text.IndexOf('\n', start);
                var next = end < 0 ? text.Length + 1 : end + 1;
                end = end < 0 ? text.Length : end;
                var length = end > start && text[end - 1] == '\r' ? end - start - 1 : end - start;
                lines.Add(new Segment(MatchTarget.Content, text.Substring(start, length), start));
                start = next;
            }

            return lines;
        }

        /// <summary>Groups consecutive lines into units: a unit starts where <paramref name="starts"/> says; separator lines end it.</summary>
        private IEnumerable<Segment> Group(IReadOnlyList<Segment> lines, Func<Segment, int, bool> starts, Func<Segment, bool> separator)
        {
            var first = -1;
            for (var i = 0; i <= lines.Count; i++)
            {
                var boundary = i == lines.Count || separator(lines[i]) || starts(lines[i], i);
                if (boundary && first >= 0)
                {
                    yield return Join(lines, first, i - 1);
                    first = -1;
                }

                if (i < lines.Count && !separator(lines[i]) && first < 0)
                {
                    first = i;
                }
            }
        }

        private Segment Join(IReadOnlyList<Segment> lines, int first, int last)
        {
            var start = lines[first].Offset;
            var end = lines[last].Offset + lines[last].Text.Length;
            return new Segment(MatchTarget.Content, Note.Text[start..end], start);
        }

        private static bool IsBlank(Segment line) => string.IsNullOrWhiteSpace(line.Text);

        /// <summary>List items and headings start their own block.</summary>
        private static bool StartsBlock(Segment line, int index)
        {
            var text = line.Text.TrimStart();
            if (text.StartsWith('#'))
            {
                return true;
            }

            if (text.Length > 1 && text[0] is '-' or '*' or '+' && text[1] == ' ')
            {
                return true;
            }

            var digits = 0;
            while (digits < text.Length && char.IsAsciiDigit(text[digits]))
            {
                digits++;
            }

            return digits > 0 && digits + 1 < text.Length && text[digits] is '.' or ')' && text[digits + 1] == ' ';
        }
    }
}
