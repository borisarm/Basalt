using Basalt.Domain.Notes;
using Basalt.Testing.Generators;
using FsCheck.Fluent;

namespace Basalt.Domain.Tests.Notes;

public sealed class VaultPathPropertyTests
{
    [BasaltProperty]
    [Trait("Spec", "DOM-3.1")]
    public bool Create_RoundTripsThroughToString(VaultPath path) =>
        VaultPath.Create(path.ToString()).Value == path;

    [BasaltProperty]
    public bool Create_IsIdempotentOnWindowsSeparators(VaultPath path) =>
        VaultPath.Create(path.ToString().Replace('/', '\\')).Value == path;

    [BasaltProperty]
    public bool Parent_IsAncestor(VaultPath path) =>
        path.IsRoot || path.IsDescendantOf(path.Parent);

    [BasaltProperty]
    public bool CaseFoldKey_EqualForUpperCasedPath(VaultPath path)
    {
        var upper = VaultPath.Create(path.ToString().ToUpperInvariant());
        return upper.IsFailure || upper.Value.CaseFoldKey == path.CaseFoldKey;
    }

    [BasaltProperty]
    public bool RelativeTo_Parent_IsName(VaultPath path) =>
        path.IsRoot || path.RelativeTo(path.Parent) == path.Name;

    [Fact]
    public void Generator_ProducesNonTrivialPaths()
    {
        var samples = BasaltArbitraries.VaultPath().Generator.Sample(200, new FsCheck.Rnd(20261019), 50);

        samples.Should().Contain(p => p.Segments.Count > 1);
        samples.Should().OnlyContain(p => VaultPath.Create(p.ToString()).IsSuccess);
    }
}
