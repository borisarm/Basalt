using BenchmarkDotNet.Running;

namespace Basalt.Benchmarks;

/// <summary>
/// NFR benchmarks of spec 11: search (NFR-004), quick switcher (NFR-005) and local sync scan (NFR-010).
/// Run with <c>dotnet run --project tests/Basalt.Benchmarks -c Release</c>.
/// </summary>
public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
