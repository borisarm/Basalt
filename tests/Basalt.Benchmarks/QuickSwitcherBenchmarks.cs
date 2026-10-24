using Basalt.Domain.Search;
using BenchmarkDotNet.Attributes;

namespace Basalt.Benchmarks;

/// <summary>NFR-005: one quick-switcher keystroke over 10 000 titles must stay under 50 ms.</summary>
[MemoryDiagnoser]
public class QuickSwitcherBenchmarks
{
    private const int TitleCount = 10_000;
    private string[] _titles = [];

    [GlobalSetup]
    public void Setup()
    {
        _titles = Enumerable.Range(0, TitleCount)
            .Select(i => $"Carpeta {i % 300:D3}/Nota {i:D5} lorem ipsum")
            .ToArray();
    }

    [Benchmark]
    public int Rank() => FuzzyMatcher.Rank("nota0123", _titles, title => title).Count;
}
