using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Basalt.Domain.Common;

namespace Basalt.Domain.Search;

/// <summary>
/// Parses the search language of spec 08 §4.1 into a <see cref="SearchQuery"/>. Like Obsidian, <c>OR</c> binds tighter
/// than (implicit or explicit) <c>AND</c>: <c>a b OR c</c> = <c>a AND (b OR c)</c>. Parsing is lenient so that
/// half-typed queries work: unterminated phrases, groups and properties run to the end, unmatched <c>)</c> and dangling
/// <c>AND</c>/<c>OR</c> are ignored and empty groups/fields are dropped. Only invalid regexes and excessive nesting fail.
/// </summary>
public static class SearchQueryParser
{
    /// <summary>Maximum nesting of groups, negations and fields (protects the recursive parser and evaluators).</summary>
    public const int MaxDepth = 64;

    private static readonly (string Name, SearchField Field)[] Fields =
    [
        ("file", SearchField.File),
        ("path", SearchField.Path),
        ("tag", SearchField.Tag),
        ("line", SearchField.Line),
        ("block", SearchField.Block),
        ("section", SearchField.Section),
        ("content", SearchField.Content),
        ("task", SearchField.Task),
        ("task-todo", SearchField.TaskTodo),
        ("task-done", SearchField.TaskDone),
    ];

    public static Result<SearchQuery> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(text);
        var root = parser.ParseAnd(0);
        return parser.Error is { } error ? error : new SearchQuery(root);
    }

    public static string FieldName(SearchField field) => Fields.First(f => f.Field == field).Name;

    /// <summary>The regex options used to validate and evaluate <see cref="RegexNode"/> patterns (NFR-032).</summary>
    public static RegexOptions RegexOptionsFor => RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private sealed class Parser(string text)
    {
        private int _position;

        public Error? Error { get; private set; }

        private bool AtEnd => _position >= text.Length;

        private char Current => text[_position];

        /// <summary>and-expr = or-expr *( [ "AND" ] or-expr ), until the end or the closing parenthesis of a group.</summary>
        public SearchNode? ParseAnd(int depth)
        {
            var operands = ImmutableArray.CreateBuilder<SearchNode>();
            while (Error is null)
            {
                SkipWhitespace();
                if (AtEnd || (Current == ')' && depth > 0))
                {
                    break;
                }

                if (Current == ')')
                {
                    _position++; // unmatched at top level
                    continue;
                }

                if (TryKeyword("AND") || TryKeyword("OR"))
                {
                    continue; // dangling operator
                }

                if (ParseOr(depth) is { } operand)
                {
                    operands.Add(operand);
                }
            }

            return Combine(operands, isOr: false);
        }

        /// <summary>or-expr = unary *( "OR" unary ).</summary>
        private SearchNode? ParseOr(int depth)
        {
            var operands = ImmutableArray.CreateBuilder<SearchNode>();
            if (ParseUnary(depth) is { } first)
            {
                operands.Add(first);
            }

            while (Error is null)
            {
                var save = _position;
                SkipWhitespace();
                if (!TryKeyword("OR"))
                {
                    _position = save;
                    break;
                }

                while (TrySkipKeywordAfterWhitespace("OR"))
                {
                    // "a OR OR b"
                }

                SkipWhitespace();
                if (AtEnd || Current == ')' || PeekKeyword("AND"))
                {
                    break; // dangling OR
                }

                if (ParseUnary(depth) is { } operand)
                {
                    operands.Add(operand);
                }
            }

            return Combine(operands, isOr: true);
        }

        private SearchNode? ParseUnary(int depth)
        {
            if (!Enter(depth))
            {
                return null;
            }

            if (Current == '-' && _position + 1 < text.Length && !IsDelimiter(text[_position + 1]))
            {
                _position++;
                return ParseUnary(depth + 1) is { } operand ? new NotNode(operand) : null;
            }

            return ParsePrimary(depth);
        }

        private SearchNode? ParsePrimary(int depth)
        {
            if (!Enter(depth))
            {
                return null;
            }

            switch (Current)
            {
                case '(':
                    _position++;
                    var inner = ParseAnd(depth + 1);
                    if (!AtEnd && Current == ')')
                    {
                        _position++;
                    }

                    return inner is null ? null : new GroupNode(inner);
                case '"':
                    return new PhraseNode(ReadQuoted());
                case '[':
                    return ParseProperty();
                case '/' when TryReadRegex(out var pattern):
                    return Validate(pattern) ? new RegexNode(pattern) : null;
            }

            if (TryReadField(out var field))
            {
                if (AtEnd || IsDelimiter(Current))
                {
                    return null; // "file:" without operand
                }

                return ParsePrimary(depth + 1) is { } operand ? new FieldNode(field, operand) : null;
            }

            return ReadTerm();
        }

        private bool Enter(int depth)
        {
            if (depth < MaxDepth)
            {
                return true;
            }

            Error ??= new Error(DomainErrors.SearchTooComplex, "The query is nested too deeply.");
            _position = text.Length;
            return false;
        }

        private TermNode? ReadTerm()
        {
            var start = _position;
            while (!AtEnd && !char.IsWhiteSpace(Current) && Current is not ('"' or '(' or ')'))
            {
                _position++;
            }

            return _position > start ? new TermNode(text[start.._position]) : null;
        }

        /// <summary>Reads <c>"..."</c> (current char is the opening quote) with <c>\"</c> and <c>\\</c> escapes.</summary>
        private string ReadQuoted()
        {
            _position++;
            var builder = new StringBuilder();
            while (!AtEnd && Current != '"')
            {
                if (Current == '\\' && _position + 1 < text.Length && text[_position + 1] is '"' or '\\')
                {
                    _position++;
                }

                builder.Append(Current);
                _position++;
            }

            if (!AtEnd)
            {
                _position++;
            }

            return builder.ToString();
        }

        /// <summary><c>/pattern/</c> followed by a delimiter; <c>\/</c> escapes a slash. Otherwise it is a term.</summary>
        private bool TryReadRegex(out string pattern)
        {
            pattern = string.Empty;
            for (var i = _position + 1; i < text.Length; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                    continue;
                }

                if (text[i] != '/')
                {
                    continue;
                }

                if (i == _position + 1 || (i + 1 < text.Length && !IsDelimiter(text[i + 1])))
                {
                    return false;
                }

                pattern = text[(_position + 1)..i].Replace("\\/", "/", StringComparison.Ordinal);
                _position = i + 1;
                return true;
            }

            return false;
        }

        private bool Validate(string pattern)
        {
            try
            {
                _ = new Regex(pattern, RegexOptionsFor);
                return true;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                Error ??= new Error(DomainErrors.SearchInvalidRegex, "The regular expression is not valid.").With("pattern", pattern);
                return false;
            }
        }

        private bool TryReadField(out SearchField field)
        {
            foreach (var (name, candidate) in Fields.OrderByDescending(f => f.Name.Length))
            {
                if (string.Compare(text, _position, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
                    && _position + name.Length < text.Length
                    && text[_position + name.Length] == ':')
                {
                    _position += name.Length + 1;
                    field = candidate;
                    return true;
                }
            }

            field = default;
            return false;
        }

        /// <summary><c>[key]</c>, <c>[key:value]</c>, <c>[key:"quoted"]</c>, <c>[key:null]</c>.</summary>
        private PropertyNode? ParseProperty()
        {
            _position++;
            var key = new StringBuilder();
            while (!AtEnd && Current is not (']' or ':'))
            {
                key.Append(Current);
                _position++;
            }

            string? value = null;
            var quoted = false;
            if (!AtEnd && Current == ':')
            {
                _position++;
                while (!AtEnd && Current == ' ')
                {
                    _position++;
                }

                if (!AtEnd && Current == '"')
                {
                    value = ReadQuoted();
                    quoted = true;
                }

                var rest = new StringBuilder();
                while (!AtEnd && Current != ']')
                {
                    rest.Append(Current);
                    _position++;
                }

                if (!quoted)
                {
                    value = rest.ToString().Trim();
                }
            }

            if (!AtEnd)
            {
                _position++; // ']'
            }

            var name = key.ToString().Trim();
            if (name.Length == 0)
            {
                return null;
            }

            return value switch
            {
                null or "" when !quoted => new PropertyNode(name, PropertyTest.Exists),
                "null" when !quoted => new PropertyNode(name, PropertyTest.IsEmpty),
                _ => new PropertyNode(name, PropertyTest.Contains, value),
            };
        }

        private bool TryKeyword(string keyword)
        {
            if (!PeekKeyword(keyword))
            {
                return false;
            }

            _position += keyword.Length;
            return true;
        }

        private bool TrySkipKeywordAfterWhitespace(string keyword)
        {
            var save = _position;
            SkipWhitespace();
            if (TryKeyword(keyword))
            {
                return true;
            }

            _position = save;
            return false;
        }

        /// <summary>An upper-case keyword as a whole token.</summary>
        private bool PeekKeyword(string keyword) =>
            string.CompareOrdinal(text, _position, keyword, 0, keyword.Length) == 0
            && (_position + keyword.Length == text.Length || IsDelimiter(text[_position + keyword.Length]) || text[_position + keyword.Length] == '(');

        private void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(Current))
            {
                _position++;
            }
        }

        private static bool IsDelimiter(char c) => char.IsWhiteSpace(c) || c == ')';

        private static SearchNode? Combine(ImmutableArray<SearchNode>.Builder operands, bool isOr) => operands.Count switch
        {
            0 => null,
            1 => operands[0],
            _ => isOr ? new OrNode(operands.ToImmutable()) : new AndNode(operands.ToImmutable()),
        };
    }
}
