using System.Reflection;
using System.Runtime.CompilerServices;

namespace Basalt.Architecture.Tests.Rules;

/// <summary>Reflection-based conventions over types (R6, R7 and spec 09 §8 conventions).</summary>
internal static class TypeRules
{
    private static readonly string[] AllowedPortAssemblyPrefixes = ["Basalt.", "System.", "Microsoft.Extensions."];

    /// <summary>R6: adapter types must be internal except the DI registration extension class.</summary>
    public static IReadOnlyList<string> PublicAdapterTypes(Assembly assembly) =>
        [.. assembly.GetExportedTypes()
            .Where(t => !(t.IsAbstract && t.IsSealed && t.Name.EndsWith("ServiceCollectionExtensions", StringComparison.Ordinal)))
            .Select(t => t.FullName!)];

    /// <summary>R7: port signatures only use BCL or Basalt types.</summary>
    public static IReadOnlyList<string> PortsExposingExternalTypes(IEnumerable<Type> ports)
    {
        var violations = new List<string>();
        foreach (var port in ports)
        {
            foreach (var method in port.GetMethods())
            {
                var types = method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType);
                foreach (var type in types.SelectMany(Flatten))
                {
                    if (!IsAllowedPortType(type))
                    {
                        violations.Add($"{port.FullName}.{method.Name} exposes {type.FullName}");
                    }
                }
            }
        }

        return violations;
    }

    public static IEnumerable<Type> PortInterfaces(Assembly assembly, string namespacePrefix) =>
        assembly.GetTypes().Where(t => t.IsInterface && t.Namespace?.StartsWith(namespacePrefix, StringComparison.Ordinal) == true);

    /// <summary>Use-case handlers must be sealed.</summary>
    public static IReadOnlyList<string> UnsealedHandlers(Assembly assembly) =>
        [.. assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsSealed && IsHandler(t))
            .Select(t => t.FullName!)];

    /// <summary>No <c>async void</c> outside Basalt.UI.</summary>
    public static IReadOnlyList<string> AsyncVoidMethods(Assembly assembly) =>
        [.. assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => m.ReturnType == typeof(void) && m.GetCustomAttribute<AsyncStateMachineAttribute>() is not null)
            .Select(m => $"{m.DeclaringType!.FullName}.{m.Name}")];

    /// <summary>Every concrete ViewModel derives from the given base type.</summary>
    public static IReadOnlyList<string> ViewModelsNotDerivingFrom(Assembly assembly, Type baseType) =>
        [.. assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("ViewModel", StringComparison.Ordinal) && !baseType.IsAssignableFrom(t))
            .Select(t => t.FullName!)];

    private static bool IsHandler(Type type) =>
        type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition().Name is "ICommandHandler`2" or "IQueryHandler`2");

    private static IEnumerable<Type> Flatten(Type type)
    {
        if (type.HasElementType)
        {
            foreach (var inner in Flatten(type.GetElementType()!))
            {
                yield return inner;
            }

            yield break;
        }

        if (type.IsGenericParameter)
        {
            yield break;
        }

        yield return type;
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments().SelectMany(Flatten))
            {
                yield return argument;
            }
        }
    }

    private static bool IsAllowedPortType(Type type)
    {
        var assemblyName = type.Assembly.GetName().Name!;
        if (assemblyName.StartsWith("System.Net.Http", StringComparison.Ordinal))
        {
            return false;
        }

        return assemblyName is "System.Private.CoreLib" or "netstandard" or "mscorlib"
            || AllowedPortAssemblyPrefixes.Any(p => assemblyName.StartsWith(p, StringComparison.Ordinal));
    }
}
