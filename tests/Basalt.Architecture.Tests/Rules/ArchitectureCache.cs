using System.Collections.Concurrent;
using System.Reflection;
using ArchUnitNET.Loader;

namespace Basalt.Architecture.Tests.Rules;

internal static class ArchitectureCache
{
    private static readonly ConcurrentDictionary<string, ArchUnitNET.Domain.Architecture> Cache = new();

    public static ArchUnitNET.Domain.Architecture Get(Assembly assembly) =>
        Cache.GetOrAdd(assembly.FullName!, _ => new ArchLoader().LoadAssemblies(assembly).Build());
}
