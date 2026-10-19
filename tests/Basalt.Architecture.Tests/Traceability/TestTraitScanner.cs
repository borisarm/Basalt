using System.Text.RegularExpressions;

namespace Basalt.Architecture.Tests.Traceability;

/// <summary>Collects the <c>[Trait("Spec", "...")]</c> identifiers present in the test sources.</summary>
internal static partial class TestTraitScanner
{
    public static IReadOnlySet<string> CoveredIds(string testsDirectory)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match m in SpecTraitRegex().Matches(File.ReadAllText(file)))
            {
                ids.Add(m.Groups["id"].Value);
            }
        }

        return ids;
    }

    [GeneratedRegex("""Trait\(\s*"Spec"\s*,\s*"(?<id>[^"]+)"\s*\)""")]
    private static partial Regex SpecTraitRegex();
}
