using System.Reflection;

namespace Basalt.Architecture.Tests.Rules;

/// <summary>Dependency rules expressed over assembly references (spec 01 §2.2).</summary>
internal static class AssemblyReferenceRules
{
    private static readonly string[] FrameworkPrefixes = ["System", "Microsoft.CSharp", "netstandard", "mscorlib"];

    public static IReadOnlyList<string> References(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(a => a.Name!)];

    /// <summary>R1: only BCL references are allowed.</summary>
    public static IReadOnlyList<string> NonFrameworkReferences(Assembly assembly) =>
        [.. References(assembly).Where(name => !Matches(name, FrameworkPrefixes))];

    public static IReadOnlyList<string> ForbiddenReferences(Assembly assembly, IEnumerable<string> forbiddenPrefixes) =>
        [.. References(assembly).Where(name => Matches(name, forbiddenPrefixes))];

    private static bool Matches(string name, IEnumerable<string> prefixes) =>
        prefixes.Any(p => name == p || name.StartsWith(p + ".", StringComparison.Ordinal));
}
