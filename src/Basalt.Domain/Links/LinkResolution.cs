using Basalt.Domain.Notes;

namespace Basalt.Domain.Links;

/// <summary>Outcome of resolving a link target (spec 02 §5.1).</summary>
public abstract record LinkResolution
{
    private LinkResolution()
    {
    }

    /// <summary>The file the link points to, or null when unresolved.</summary>
    public abstract VaultPath? TargetPath { get; }

    public sealed record Resolved(VaultPath Path) : LinkResolution
    {
        public override VaultPath? TargetPath => Path;
    }

    public sealed record Unresolved(string Target) : LinkResolution
    {
        public override VaultPath? TargetPath => null;
    }

    /// <summary>Several candidates: resolves to the preferred one but the ambiguity is reported.</summary>
    public sealed record Ambiguous(VaultPath Path, IReadOnlyList<VaultPath> Candidates) : LinkResolution
    {
        public override VaultPath? TargetPath => Path;
    }
}
