using System.Text.RegularExpressions;
using Basalt.Domain.Common;

namespace Basalt.Domain.Tags;

/// <summary>
/// A tag without '#': segments of letters, digits, '_' and '-' separated by '/', with at least one non-digit.
/// Equality is case-insensitive; the original spelling is kept for display (spec 02 §6, 05 §4).
/// </summary>
public sealed partial class Tag : IEquatable<Tag>
{
    private Tag(string name)
    {
        Name = name;
        FoldKey = name.ToUpperInvariant();
    }

    public string Name { get; }

    public string FoldKey { get; }

    public IReadOnlyList<string> Segments => Name.Split('/');

    /// <returns><c>tag.invalid</c> when the text is not a valid tag. A leading '#' and a trailing '/' are dropped.</returns>
    public static Result<Tag> Create(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var name = text.StartsWith('#') ? text[1..] : text;
        if (name.EndsWith('/'))
        {
            name = name[..^1];
        }

        return TagRegex().IsMatch(name) && name.Any(c => c != '/' && !char.IsDigit(c))
            ? new Tag(name)
            : new Error(DomainErrors.TagInvalid, "Tags contain letters, digits, '_', '-' and '/' and at least one non-digit.");
    }

    /// <summary>"a/b/c" → a, a/b, a/b/c.</summary>
    public IEnumerable<Tag> SelfAndAncestors()
    {
        var segments = Segments;
        for (var i = 1; i <= segments.Count; i++)
        {
            yield return new Tag(string.Join('/', segments.Take(i)));
        }
    }

    /// <summary>True for the tag itself and nested tags (<c>tag:#a</c> matches <c>#a/b</c>).</summary>
    public bool IsSelfOrDescendantOf(Tag ancestor)
    {
        ArgumentNullException.ThrowIfNull(ancestor);
        return FoldKey == ancestor.FoldKey || FoldKey.StartsWith(ancestor.FoldKey + "/", StringComparison.Ordinal);
    }

    public bool Equals(Tag? other) => other is not null && FoldKey == other.FoldKey;

    public override bool Equals(object? obj) => Equals(obj as Tag);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(FoldKey);

    public static bool operator ==(Tag? left, Tag? right) => Equals(left, right);

    public static bool operator !=(Tag? left, Tag? right) => !Equals(left, right);

    public override string ToString() => "#" + Name;

    // \p{M}: combining marks of decomposed (NFD) letters belong to the letter.
    [GeneratedRegex(@"^[\p{L}\p{M}\p{N}_\-]+(/[\p{L}\p{M}\p{N}_\-]+)*$")]
    private static partial Regex TagRegex();
}
