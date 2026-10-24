using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;
using Basalt.Domain.Search;
using BenchmarkDotNet.Attributes;

namespace Basalt.Benchmarks;

/// <summary>NFR-004: first results for a query over 10 000 notes must stay under 300 ms.</summary>
[MemoryDiagnoser]
public class SearchBenchmarks
{
    private const int NoteCount = 10_000;
    private SearchableNote[] _notes = [];
    private INoteMatcher _matcher = null!;

    [GlobalSetup]
    public void Setup()
    {
        _notes = Enumerable.Range(0, NoteCount)
            .Select(i => new SearchableNote(
                VaultPath.Create($"Carpeta {i % 300:D3}/Nota {i:D5}.md").Value,
                $"# Nota {i:D5}\n\nLorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor.",
                NoteMetadata.Empty))
            .ToArray();
        _matcher = NoteMatcher.Create(SearchQueryParser.Parse("magna aliqua OR consectetur").Value);
    }

    [Benchmark]
    public int MatchAll()
    {
        var matches = 0;
        foreach (var note in _notes)
        {
            if (_matcher.Match(note) is not null)
            {
                matches++;
            }
        }

        return matches;
    }
}
