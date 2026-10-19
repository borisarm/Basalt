using Basalt.Domain.Notes;

namespace Basalt.Domain.Links;

/// <summary>
/// Read-only view of the files of a vault used to resolve links (spec 02 §5.1). Implemented in Application over the
/// metadata cache.
/// </summary>
public interface IVaultFileCatalog
{
    /// <summary>Files whose name (with extension) equals <paramref name="fileName"/> ignoring case.</summary>
    IReadOnlyCollection<VaultPath> FindByName(string fileName);

    /// <summary>Files whose path equals <paramref name="path"/> ignoring case (several only on case-sensitive file systems).</summary>
    IReadOnlyCollection<VaultPath> FindByPath(VaultPath path);
}
