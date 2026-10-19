using System.Text.RegularExpressions;

namespace Basalt.Architecture.Tests.Traceability;

/// <summary>Extracts the traceable identifiers from the specification (spec 09 §9).</summary>
internal static partial class SpecCatalog
{
    public static IReadOnlySet<string> RequiredIds(string specsDirectory)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        var functional = File.ReadAllText(Path.Combine(specsDirectory, "04-especificacion-funcional.md"));
        foreach (Match m in ScenarioRegex().Matches(functional))
        {
            ids.Add(m.Groups["id"].Value);
        }

        var testing = File.ReadAllText(Path.Combine(specsDirectory, "09-estrategia-de-pruebas.md"));
        foreach (Match m in SimulationScenarioRegex().Matches(testing))
        {
            ids.Add(m.Groups["id"].Value);
        }

        var sync = File.ReadAllText(Path.Combine(specsDirectory, "06-sincronizacion-github.md"));
        foreach (Match m in DecisionRowRegex().Matches(sync))
        {
            ids.Add($"SYNC-ROW-{m.Groups["row"].Value}");
        }

        for (var p = 1; p <= 5; p++)
        {
            ids.Add($"SYNC-P{p}");
        }

        var nfr = File.ReadAllText(Path.Combine(specsDirectory, "11-requisitos-no-funcionales.md"));
        foreach (Match m in NfrTableRowRegex().Matches(nfr))
        {
            if (!m.Groups["verification"].Value.TrimStart().StartsWith("Manual", StringComparison.Ordinal))
            {
                ids.Add(m.Groups["id"].Value);
            }
        }

        foreach (Match m in NfrBulletRegex().Matches(nfr))
        {
            ids.Add(m.Groups["id"].Value);
        }

        return ids;
    }

    [GeneratedRegex(@"Escenario: (?<id>F-\d{3}\.\d+)")]
    private static partial Regex ScenarioRegex();

    [GeneratedRegex(@"^\| (?<id>S\d{2}) \|", RegexOptions.Multiline)]
    private static partial Regex SimulationScenarioRegex();

    // Rows of the decision table in 06 §5: "| 1 | b | b | b | ..."
    [GeneratedRegex(@"^\| (?<row>\d{1,2}) \| [^|]+ \| [^|]+ \| [^|]+ \|", RegexOptions.Multiline)]
    private static partial Regex DecisionRowRegex();

    [GeneratedRegex(@"^\| (?<id>NFR-\d{3}) \|[^|]*\|[^|]*\|(?<verification>[^|]*)\|", RegexOptions.Multiline)]
    private static partial Regex NfrTableRowRegex();

    [GeneratedRegex(@"^- (?<id>NFR-\d{3})\.", RegexOptions.Multiline)]
    private static partial Regex NfrBulletRegex();
}
