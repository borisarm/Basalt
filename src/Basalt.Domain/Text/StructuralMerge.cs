using Basalt.Domain.Common;

namespace Basalt.Domain.Text;

/// <summary>Outcome of a structural (key/element level) merge.</summary>
public abstract record StructuralMergeResult
{
    private StructuralMergeResult()
    {
    }

    public sealed record Merged(StructuredValue Value) : StructuralMergeResult;

    /// <summary>Paths changed differently on both sides ("a.b", "$" for the root).</summary>
    public sealed record Conflict(IReadOnlyList<string> Paths) : StructuralMergeResult;
}

/// <summary>Key-level merge of frontmatter (spec 02 §10): succeeds when the sets of changed keys are compatible.</summary>
public interface IFrontmatterMerger
{
    StructuralMergeResult Merge(StructuredValue.MapValue @base, StructuredValue.MapValue ours, StructuredValue.MapValue theirs);
}

/// <summary>Key/element-level merge of shared JSON settings such as bookmarks and property types (spec 08 §1.1).</summary>
public interface IJsonStructuralMerger
{
    StructuralMergeResult Merge(StructuredValue @base, StructuredValue ours, StructuredValue theirs);
}

/// <inheritdoc />
public sealed class FrontmatterMerger : IFrontmatterMerger
{
    public StructuralMergeResult Merge(StructuredValue.MapValue @base, StructuredValue.MapValue ours, StructuredValue.MapValue theirs) =>
        StructuralMerging.MergeMaps(@base, ours, theirs, string.Empty, recurse: false);
}

/// <inheritdoc />
public sealed class JsonStructuralMerger : IJsonStructuralMerger
{
    public StructuralMergeResult Merge(StructuredValue @base, StructuredValue ours, StructuredValue theirs)
    {
        ArgumentNullException.ThrowIfNull(@base);
        ArgumentNullException.ThrowIfNull(ours);
        ArgumentNullException.ThrowIfNull(theirs);
        var conflicts = new List<string>();
        var merged = StructuralMerging.MergeValue(@base, ours, theirs, string.Empty, recurse: true, conflicts);
        return conflicts.Count == 0 ? new StructuralMergeResult.Merged(merged!) : new StructuralMergeResult.Conflict(conflicts);
    }
}

internal static class StructuralMerging
{
    public static StructuralMergeResult MergeMaps(StructuredValue.MapValue @base, StructuredValue.MapValue ours, StructuredValue.MapValue theirs, string path, bool recurse)
    {
        ArgumentNullException.ThrowIfNull(@base);
        ArgumentNullException.ThrowIfNull(ours);
        ArgumentNullException.ThrowIfNull(theirs);
        var conflicts = new List<string>();
        var merged = MergeMapEntries(@base, ours, theirs, path, recurse, conflicts);
        return conflicts.Count == 0 ? new StructuralMergeResult.Merged(merged) : new StructuralMergeResult.Conflict(conflicts);
    }

    /// <summary>Three-way merge of one value; null means "absent". Conflicts are collected with their path.</summary>
    public static StructuredValue? MergeValue(StructuredValue? b, StructuredValue? o, StructuredValue? t, string path, bool recurse, List<string> conflicts)
    {
        if (Equals(o, t))
        {
            return o;
        }

        if (Equals(o, b))
        {
            return t;
        }

        if (Equals(t, b))
        {
            return o;
        }

        if (recurse && o is StructuredValue.MapValue om && t is StructuredValue.MapValue tm)
        {
            return MergeMapEntries(b as StructuredValue.MapValue ?? StructuredValue.MapValue.Of(), om, tm, path, recurse, conflicts);
        }

        if (recurse && o is StructuredValue.ListValue ol && t is StructuredValue.ListValue tl)
        {
            return MergeLists(b as StructuredValue.ListValue ?? new StructuredValue.ListValue([]), ol, tl);
        }

        conflicts.Add(path.Length == 0 ? "$" : path);
        return o;
    }

    private static StructuredValue.MapValue MergeMapEntries(StructuredValue.MapValue b, StructuredValue.MapValue o, StructuredValue.MapValue t, string path, bool recurse, List<string> conflicts)
    {
        var keys = o.Keys.Concat(t.Keys.Where(k => o.TryGet(k) is null && b.TryGet(k) is null)).Concat(b.Keys).Distinct(StringComparer.Ordinal).ToList();
        var entries = new List<KeyValuePair<string, StructuredValue>>();
        foreach (var key in keys)
        {
            var value = MergeValue(b.TryGet(key), o.TryGet(key), t.TryGet(key), path.Length == 0 ? key : $"{path}.{key}", recurse, conflicts);
            if (value is not null)
            {
                entries.Add(KeyValuePair.Create(key, value));
            }
        }

        return new StructuredValue.MapValue(entries);
    }

    // Element-level merge: keep ours' order, drop elements theirs removed, append elements theirs added.
    private static StructuredValue.ListValue MergeLists(StructuredValue.ListValue b, StructuredValue.ListValue o, StructuredValue.ListValue t)
    {
        var removedByTheirs = b.Items.Where(item => !t.Items.Contains(item)).ToList();
        var result = o.Items.Where(item => !removedByTheirs.Contains(item)).ToList();
        foreach (var item in t.Items)
        {
            if (!b.Items.Contains(item) && !result.Contains(item))
            {
                result.Add(item);
            }
        }

        return new StructuredValue.ListValue(result);
    }
}
