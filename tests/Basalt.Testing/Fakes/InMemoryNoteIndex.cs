using System.Collections.Concurrent;
using Basalt.Application.Ports.Index;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;

namespace Basalt.Testing.Fakes;

/// <summary>In-memory <see cref="INoteIndex"/> (spec 03 §1.3); also the oracle for search equivalence tests.</summary>
public sealed class InMemoryNoteIndex : INoteIndex
{
    private readonly ConcurrentDictionary<VaultPath, IndexedNote> _notes = new();

    public int AnalyzerVersion { get; private set; }

    public IReadOnlyCollection<IndexedNote> Notes => [.. _notes.Values];

    public Task UpsertAsync(IndexedNote note, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(note);
        _notes[note.Path] = note;
        return Task.CompletedTask;
    }

    public Task UpsertManyAsync(IReadOnlyList<IndexedNote> notes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notes);
        foreach (var note in notes)
        {
            _notes[note.Path] = note;
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(VaultPath path, CancellationToken ct)
    {
        _notes.TryRemove(path, out _);
        return Task.CompletedTask;
    }

    public Task RenameAsync(VaultPath from, VaultPath to, CancellationToken ct)
    {
        if (_notes.TryRemove(from, out var note))
        {
            _notes[to] = note with { Path = to, Kind = FileClassifier.Classify(to) };
        }

        return Task.CompletedTask;
    }

    public Task<IndexSnapshotInfo> GetSnapshotInfoAsync(CancellationToken ct) =>
        Task.FromResult(new IndexSnapshotInfo(AnalyzerVersion, _notes.ToDictionary(kv => kv.Key, kv => kv.Value.Stamp)));

    public Task ResetAsync(int analyzerVersion, CancellationToken ct)
    {
        _notes.Clear();
        AnalyzerVersion = analyzerVersion;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IndexedNoteSummary>> GetAllSummariesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IndexedNoteSummary>>([.. _notes.Values.Select(n => new IndexedNoteSummary(n.Path, n.Kind, n.Stamp, n.Metadata))]);

    public Task<IndexedNote?> GetAsync(VaultPath path, CancellationToken ct) =>
        Task.FromResult(_notes.GetValueOrDefault(path));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Hands out one <see cref="InMemoryNoteIndex"/> per vault (kept across close/re-open like a file).</summary>
public sealed class InMemoryNoteIndexFactory : INoteIndexFactory
{
    private readonly ConcurrentDictionary<VaultId, InMemoryNoteIndex> _indexes = new();

    public InMemoryNoteIndex For(VaultId vault) => _indexes.GetOrAdd(vault, _ => new InMemoryNoteIndex());

    public Task<INoteIndex> OpenAsync(VaultId vault, CancellationToken ct) => Task.FromResult<INoteIndex>(For(vault));
}
