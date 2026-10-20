using System.Globalization;
using Basalt.Domain.Search;

namespace Basalt.Adapters.Index;

/// <summary>
/// Translates a search AST into a SQL condition over <c>files f</c> that over-approximates the matching set (spec 08
/// §4.3 step 2): terms and phrases become trigram FTS5 phrase queries over folded text (an exact substring test),
/// <c>tag:</c> and <c>[property]</c> become lookups in their tables, and anything that cannot be translated (regexes,
/// negations, terms shorter than three characters) leaves its subtree unconstrained. The exact answer is then computed by
/// <c>NoteMatcher</c> on the candidates.
/// </summary>
internal sealed class FtsQueryBuilder
{
    private readonly List<(string Name, object Value)> _parameters = [];

    private enum Scope
    {
        Default,
        Name,
        Path,
        Body,
        Tags,
    }

    public IReadOnlyList<(string Name, object Value)> Parameters => _parameters;

    /// <returns>A SQL boolean expression, or null when every file is a candidate.</returns>
    public string? Build(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Root is null ? null : Visit(query.Root, Scope.Default);
    }

    private string? Visit(SearchNode node, Scope scope) => node switch
    {
        TermNode term => Text(term.Text, scope),
        PhraseNode phrase => Text(phrase.Text, scope),
        AndNode and => And(and.Operands.Select(o => Visit(o, scope)).OfType<string>().ToList()),
        OrNode or => Or([.. or.Operands.Select(o => Visit(o, scope))]),
        GroupNode group => Visit(group.Inner, scope),
        FieldNode field => Visit(field.Operand, ScopeOf(field.Field)),
        PropertyNode property => $"f.id IN (SELECT file_id FROM properties WHERE key_fold = {Parameter(property.Key.ToUpperInvariant())})",
        _ => null, // regex, negation
    };

    private string? Text(string text, Scope scope)
    {
        var folded = FoldedText.Fold(text);
        if (scope == Scope.Tags)
        {
            var tag = folded.TrimStart('#');
            if (tag.Length == 0)
            {
                return null;
            }

            var exact = Parameter(tag);
            var prefix = Parameter(tag + "/");
            return $"f.id IN (SELECT file_id FROM tags WHERE tag_search = {exact} OR substr(tag_search, 1, length({prefix})) = {prefix})";
        }

        if (folded.EnumerateRunes().Count() < 3)
        {
            return null; // the trigram index cannot answer substrings shorter than three characters
        }

        var columns = scope switch
        {
            Scope.Name => "name",
            Scope.Path => "path",
            Scope.Body => "body",
            _ => "{name body}",
        };
        var match = Parameter($"{columns} : {Quote(folded)}");
        return $"f.id IN (SELECT rowid FROM content_fts WHERE content_fts MATCH {match})";
    }

    private static string Quote(string token) => "\"" + token.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string? And(List<string> parts) => parts.Count switch
    {
        0 => null,
        1 => parts[0],
        _ => "(" + string.Join(" AND ", parts) + ")",
    };

    private static string? Or(List<string?> parts) =>
        parts.Any(p => p is null) ? null : "(" + string.Join(" OR ", parts) + ")";

    private static Scope ScopeOf(SearchField field) => field switch
    {
        SearchField.File => Scope.Name,
        SearchField.Path => Scope.Path,
        SearchField.Tag => Scope.Tags,
        _ => Scope.Body, // content, line, block, section and task operators all look inside the text
    };

    private string Parameter(object value)
    {
        var name = "$p" + _parameters.Count.ToString(CultureInfo.InvariantCulture);
        _parameters.Add((name, value));
        return name;
    }
}
