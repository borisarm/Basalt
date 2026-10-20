using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;
using Basalt.Domain.Search;
using Basalt.Domain.Sync;

namespace Basalt.Application.Ports.Index;

/// <summary>What the index knows about a file's version, to decide whether it must be re-analysed.</summary>
public sealed record IndexStamp(long Size, DateTimeOffset LastWriteUtc, BlobSha? Sha);

/// <summary>An indexed vault file. <see cref="Text"/> is set for notes (full-text search); attachments have none.</summary>
public sealed record IndexedNote(VaultPath Path, FileKind Kind, IndexStamp Stamp, string? Text, NoteMetadata Metadata);

/// <summary>An indexed file without its text (used to build the in-memory metadata cache at open).</summary>
public sealed record IndexedNoteSummary(VaultPath Path, FileKind Kind, IndexStamp Stamp, NoteMetadata Metadata);

/// <summary>State of the persisted index, for incremental re-indexing at open (spec 08 §2.2).</summary>
public sealed record IndexSnapshotInfo(int AnalyzerVersion, IReadOnlyDictionary<VaultPath, IndexStamp> Entries);

/// <summary>Result order of <see cref="INoteIndexReader.SearchAsync"/>.</summary>
public enum SearchSort
{
    /// <summary>Score descending (file-name matches first, then number of matches), then path.</summary>
    Relevance,
    NameAscending,
    NameDescending,
    ModifiedNewest,
    ModifiedOldest,
}

/// <param name="MatchCase">Case- and accent-sensitive terms, phrases and regexes (F-007 "match case" option).</param>
/// <param name="Limit">Maximum number of hits (after ordering); null for all.</param>
public sealed record SearchOptions(SearchSort Sort = SearchSort.Relevance, bool MatchCase = false, int? Limit = null)
{
    public static SearchOptions Default { get; } = new();
}

/// <summary>A matching file with the ranges to highlight (spec 08 §4.3).</summary>
public sealed record SearchHit(VaultPath Path, FileKind Kind, DateTimeOffset LastWriteUtc, IReadOnlyList<MatchRange> Ranges, double Score);

/// <summary>Writes to the persistent index of one vault (spec 03 §1.3).</summary>
public interface INoteIndexWriter
{
    Task UpsertAsync(IndexedNote note, CancellationToken ct);

    /// <summary>Upserts several notes in one transaction (batches of the initial scan).</summary>
    Task UpsertManyAsync(IReadOnlyList<IndexedNote> notes, CancellationToken ct);

    Task RemoveAsync(VaultPath path, CancellationToken ct);

    Task RenameAsync(VaultPath from, VaultPath to, CancellationToken ct);

    Task<IndexSnapshotInfo> GetSnapshotInfoAsync(CancellationToken ct);

    /// <summary>Deletes everything and records the analyzer version the next entries are produced with.</summary>
    Task ResetAsync(int analyzerVersion, CancellationToken ct);
}

/// <summary>Reads from the persistent index of one vault (spec 03 §1.3).</summary>
public interface INoteIndexReader
{
    /// <summary>
    /// Every indexed file (notes and attachments) for which <c>NoteMatcher</c> matches <paramref name="query"/>: the
    /// same set for every implementation (spec 08 §4.3, equivalence with <c>InMemoryNoteIndex</c>). An empty query
    /// returns nothing.
    /// </summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, SearchOptions options, CancellationToken ct);

    Task<IReadOnlyList<IndexedNoteSummary>> GetAllSummariesAsync(CancellationToken ct);

    Task<IndexedNote?> GetAsync(VaultPath path, CancellationToken ct);
}

/// <summary>Both sides of a vault index; disposing releases the underlying storage.</summary>
public interface INoteIndex : INoteIndexReader, INoteIndexWriter, IAsyncDisposable;

/// <summary>Opens the index of a vault (<c>{AppData}/vaults/{id}/index.db</c>), recreating it if it is corrupt.</summary>
public interface INoteIndexFactory
{
    Task<INoteIndex> OpenAsync(Domain.Vaults.VaultId vault, CancellationToken ct);
}
