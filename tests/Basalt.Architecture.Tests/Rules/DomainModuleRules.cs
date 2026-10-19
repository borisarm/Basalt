using ArchUnitNET.Fluent;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Basalt.Architecture.Tests.Rules;

/// <summary>Module independence matrix of the domain (spec 09 §8).</summary>
internal static class DomainModuleRules
{
    public static IReadOnlyDictionary<string, string[]> AllowedDependencies { get; } = new Dictionary<string, string[]>
    {
        ["Common"] = [],
        ["Notes"] = ["Common"],
        ["Links"] = ["Common", "Notes"],
        ["Tags"] = ["Common", "Notes"],
        ["Search"] = ["Common", "Notes", "Tags"],
        ["Graph"] = ["Common", "Notes", "Links", "Tags"],
        ["Templates"] = ["Common", "Notes"],
        ["Text"] = ["Common"],
        ["Sync"] = ["Common", "Notes", "Text"],
        ["Vaults"] = ["Common"],
        ["Accounts"] = ["Common"],
    };

    /// <summary>Builds one rule per module forbidding dependencies on modules outside its allowed set.</summary>
    public static IEnumerable<(string Module, IArchRule Rule)> Rules(string rootNamespace, IReadOnlyDictionary<string, string[]> matrix)
    {
        foreach (var (module, allowed) in matrix)
        {
            var forbidden = matrix.Keys.Where(m => m != module && !allowed.Contains(m)).ToArray();
            if (forbidden.Length == 0)
            {
                continue;
            }

            var forbiddenPattern = $"^{Escape(rootNamespace)}\\.({string.Join('|', forbidden)})(\\..*)?$";
            var rule = Types().That().ResideInNamespaceMatching($"^{Escape(rootNamespace)}\\.{module}(\\..*)?$")
                .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(forbiddenPattern))
                .WithoutRequiringPositiveResults();
            yield return (module, rule);
        }
    }

    public static IReadOnlyList<string> Violations(ArchUnitNET.Domain.Architecture architecture, string rootNamespace, IReadOnlyDictionary<string, string[]> matrix) =>
        [.. Rules(rootNamespace, matrix)
            .Where(r => !r.Rule.HasNoViolations(architecture))
            .Select(r => r.Module)];

    private static string Escape(string ns) => ns.Replace(".", "\\.", StringComparison.Ordinal);
}
