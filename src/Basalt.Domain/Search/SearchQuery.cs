using System.Collections.Immutable;
using System.Text;

namespace Basalt.Domain.Search;

/// <summary>Field operators of the search language (spec 08 §4.2).</summary>
public enum SearchField
{
    File,
    Path,
    Tag,
    Line,
    Block,
    Section,
    Content,
    Task,
    TaskTodo,
    TaskDone,
}

/// <summary>What a <see cref="PropertyNode"/> checks.</summary>
public enum PropertyTest
{
    /// <summary><c>[key]</c>: the note has the property.</summary>
    Exists,

    /// <summary><c>[key:value]</c>: the property value contains the text.</summary>
    Contains,

    /// <summary><c>[key:null]</c>: the property exists and is empty.</summary>
    IsEmpty,
}

/// <summary>A node of the search AST. <see cref="ToString"/> is a canonical S-expression.</summary>
public abstract record SearchNode
{
    public sealed override string ToString()
    {
        var builder = new StringBuilder();
        Write(builder);
        return builder.ToString();
    }

    internal abstract void Write(StringBuilder builder);

    private protected static void Quote(StringBuilder builder, string text) =>
        builder.Append('"').Append(text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
}

/// <summary>A bare word: content or file name contains it.</summary>
public sealed record TermNode(string Text) : SearchNode
{
    internal override void Write(StringBuilder builder)
    {
        builder.Append("(term ");
        Quote(builder, Text);
        builder.Append(')');
    }
}

/// <summary><c>"exact phrase"</c>.</summary>
public sealed record PhraseNode(string Text) : SearchNode
{
    internal override void Write(StringBuilder builder)
    {
        builder.Append("(phrase ");
        Quote(builder, Text);
        builder.Append(')');
    }
}

/// <summary><c>/pattern/</c>, evaluated with <c>RegexOptions.NonBacktracking</c>.</summary>
public sealed record RegexNode(string Pattern) : SearchNode
{
    internal override void Write(StringBuilder builder)
    {
        builder.Append("(regex ");
        Quote(builder, Pattern);
        builder.Append(')');
    }
}

/// <summary>Base of n-ary nodes (structural equality over the operands).</summary>
public abstract record CompositeNode(ImmutableArray<SearchNode> Operands) : SearchNode
{
    protected abstract string Name { get; }

    public virtual bool Equals(CompositeNode? other) =>
        other is not null && other.GetType() == GetType() && Operands.SequenceEqual(other.Operands);

    public override int GetHashCode() => Operands.Aggregate(GetType().GetHashCode(), HashCode.Combine);

    internal override void Write(StringBuilder builder)
    {
        builder.Append('(').Append(Name);
        foreach (var operand in Operands)
        {
            builder.Append(' ');
            operand.Write(builder);
        }

        builder.Append(')');
    }
}

public sealed record AndNode(ImmutableArray<SearchNode> Operands) : CompositeNode(Operands)
{
    protected override string Name => "and";

    public bool Equals(AndNode? other) => base.Equals(other);

    public override int GetHashCode() => base.GetHashCode();
}

public sealed record OrNode(ImmutableArray<SearchNode> Operands) : CompositeNode(Operands)
{
    protected override string Name => "or";

    public bool Equals(OrNode? other) => base.Equals(other);

    public override int GetHashCode() => base.GetHashCode();
}

/// <summary><c>-expr</c>.</summary>
public sealed record NotNode(SearchNode Operand) : SearchNode
{
    internal override void Write(StringBuilder builder)
    {
        builder.Append("(not ");
        Operand.Write(builder);
        builder.Append(')');
    }
}

/// <summary><c>( ... )</c>; transparent for evaluation, kept for scoped operators such as <c>line:(a b)</c>.</summary>
public sealed record GroupNode(SearchNode Inner) : SearchNode
{
    internal override void Write(StringBuilder builder)
    {
        builder.Append("(group ");
        Inner.Write(builder);
        builder.Append(')');
    }
}

/// <summary><c>field:operand</c>.</summary>
public sealed record FieldNode(SearchField Field, SearchNode Operand) : SearchNode
{
    internal override void Write(StringBuilder builder)
    {
        builder.Append('(').Append(SearchQueryParser.FieldName(Field)).Append(' ');
        Operand.Write(builder);
        builder.Append(')');
    }
}

/// <summary><c>[key]</c>, <c>[key:value]</c>, <c>[key:null]</c>.</summary>
public sealed record PropertyNode(string Key, PropertyTest Test, string? Value = null) : SearchNode
{
    internal override void Write(StringBuilder builder)
    {
        builder.Append("(property ");
        Quote(builder, Key);
        switch (Test)
        {
            case PropertyTest.Contains:
                builder.Append(' ');
                Quote(builder, Value ?? string.Empty);
                break;
            case PropertyTest.IsEmpty:
                builder.Append(" null");
                break;
        }

        builder.Append(')');
    }
}

/// <summary>A parsed query; <see cref="Root"/> is null for an empty query, which matches nothing.</summary>
public sealed record SearchQuery(SearchNode? Root)
{
    public static SearchQuery Empty { get; } = new((SearchNode?)null);

    public bool IsEmpty => Root is null;

    public override string ToString() => Root?.ToString() ?? "(empty)";
}
