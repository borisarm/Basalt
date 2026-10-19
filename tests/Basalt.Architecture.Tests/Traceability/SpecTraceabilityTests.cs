namespace Basalt.Architecture.Tests.Traceability;

/// <summary>
/// Spec 09 §9: every scenario/rule identifier of the specification must be covered by at least one test tagged
/// <c>[Trait("Spec", id)]</c>. Runs in report mode until T9.10, then becomes strict.
/// </summary>
public sealed class SpecTraceabilityTests
{
    // Report mode until T9.10 (spec 10 T0.6); then this becomes `true`. BASALT_TRACE_STRICT=1 forces strict mode.
    private static readonly bool Strict = Environment.GetEnvironmentVariable("BASALT_TRACE_STRICT") == "1";

    [Fact]
    public void SpecIdentifiers_AreCoveredByTests()
    {
        var root = RepositoryRoot();
        var required = SpecCatalog.RequiredIds(Path.Combine(root, "docs", "specs"));
        var covered = TestTraitScanner.CoveredIds(Path.Combine(root, "tests"));
        var missing = required.Where(id => !covered.Contains(id)).ToList();

        TestContext.Current.SendDiagnosticMessage(
            $"Spec traceability: {required.Count - missing.Count}/{required.Count} covered. Missing: {string.Join(", ", missing)}");

        if (Strict)
        {
            missing.Should().BeEmpty("every specified scenario needs a test tagged with its identifier");
        }
    }

    [Fact]
    public void SpecCatalog_FindsAllIdentifierFamilies()
    {
        var required = SpecCatalog.RequiredIds(Path.Combine(RepositoryRoot(), "docs", "specs"));

        required.Should().Contain(["F-001.1", "F-015.7", "S01", "S32", "SYNC-ROW-1", "SYNC-ROW-15", "SYNC-P5", "NFR-002", "NFR-020"]);
        required.Should().NotContain("NFR-001", "manual NFRs are excluded");
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Basalt.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (Basalt.slnx) not found.");
    }
}
