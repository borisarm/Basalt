namespace Basalt.Domain.Text;

/// <summary>Labels written in conflict markers (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; {Local}</c> / <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt; {Remote}</c>).</summary>
public sealed record MergeOptions(string LocalLabel = "local", string RemoteLabel = "remote");

/// <summary>A region changed differently on both sides.</summary>
public sealed record ConflictRegion(
    int BaseStart,
    IReadOnlyList<string> BaseLines,
    IReadOnlyList<string> OursLines,
    IReadOnlyList<string> TheirsLines);

/// <summary>Outcome of a three-way merge (spec 02 §10).</summary>
public abstract record MergeResult(string Text)
{
    public abstract bool IsClean { get; }

    public sealed record Clean(string Text) : MergeResult(Text)
    {
        public override bool IsClean => true;
    }

    /// <summary>Text with conflict markers around every conflicting region.</summary>
    public sealed record Conflicted(string Text, IReadOnlyList<ConflictRegion> Regions) : MergeResult(Text)
    {
        public override bool IsClean => false;
    }
}
