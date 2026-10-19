using Basalt.Domain.Links;
using Basalt.Domain.Notes;

namespace Basalt.Testing.Catalogs;

/// <summary>Simple <see cref="IVaultFileCatalog"/> over a list of paths, for domain tests.</summary>
public sealed class InMemoryFileCatalog : IVaultFileCatalog
{
    private readonly List<VaultPath> _paths;

    public InMemoryFileCatalog(IEnumerable<string> paths) =>
        _paths = [.. paths.Select(p => VaultPath.Create(p).Value)];

    public InMemoryFileCatalog(params string[] paths)
        : this((IEnumerable<string>)paths)
    {
    }

    public IReadOnlyCollection<VaultPath> All => _paths;

    public IReadOnlyCollection<VaultPath> FindByName(string fileName) =>
        [.. _paths.Where(p => string.Equals(p.Name, fileName, StringComparison.OrdinalIgnoreCase))];

    public IReadOnlyCollection<VaultPath> FindByPath(VaultPath path) =>
        [.. _paths.Where(p => p.CaseFoldKey == path.CaseFoldKey)];
}
