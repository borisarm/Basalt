using System.Reflection;

namespace Basalt.Architecture.Tests.Rules;

internal static class SolutionAssemblies
{
    public static Assembly Domain => Load("Basalt.Domain");

    public static Assembly Application => Load("Basalt.Application");

    public static Assembly Presentation => Load("Basalt.Presentation");

    public static Assembly UI => Load("Basalt.UI");

    public static Assembly Composition => Load("Basalt.Composition");

    public static IReadOnlyList<Assembly> Adapters { get; } =
    [
        Load("Basalt.Adapters.FileSystem"),
        Load("Basalt.Adapters.Markdown"),
        Load("Basalt.Adapters.Index"),
        Load("Basalt.Adapters.GitHub"),
        Load("Basalt.Adapters.Platform"),
    ];

    public static Assembly Load(string name) => Assembly.Load(name);
}
