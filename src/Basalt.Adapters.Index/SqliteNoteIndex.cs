using System.Collections.Concurrent;
using System.Globalization;
using Basalt.Application.Ports.Index;
using Basalt.Application.Search;
using Basalt.Domain.Common;
using Basalt.Domain.Links;
using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;
using Basalt.Domain.Search;
using Basalt.Domain.Sync;
using Basalt.Domain.Tags;
using Microsoft.Data.Sqlite;

// CA2007 is reported for every `await using` of SQLite objects; all explicit awaits use ConfigureAwait(false).
#pragma warning disable CA2007

namespace Basalt.Adapters.Index;

/// <summary>
/// SQLite + FTS5 vault index (spec 08 §2). The index is a cache of the vault: a corrupt file or a schema written by a
/// newer version is recreated empty and re-filled by the indexer. Writes are serialised; reads use pooled connections
/// (WAL allows them to run while a write is in progress).
/// </summary>
internal sealed class SqliteNoteIndex : INoteIndex
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private SqliteNoteIndex(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = true,
            ForeignKeys = true,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public string Path { get; }

    /// <summary>True when the last open had to discard an unusable database.</summary>
    public bool WasRecreated { get; private set; }

    public static async Task<SqliteNoteIndex> OpenAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var index = new SqliteNoteIndex(path);
        if (!await index.TryInitializeAsync(ct).ConfigureAwait(false))
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }

            index.WasRecreated = true;
            if (!await index.TryInitializeAsync(ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"The index database at '{path}' cannot be created.");
            }
        }

        return index;
    }

    public Task UpsertAsync(IndexedNote note, CancellationToken ct) => UpsertManyAsync([note], ct);

    public Task UpsertManyAsync(IReadOnlyList<IndexedNote> notes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notes);
        return WriteAsync(async (connection, transaction) =>
        {
            foreach (var note in notes)
            {
                await DeleteFileAsync(connection, transaction, note.Path, ct).ConfigureAwait(false);
                await InsertFileAsync(connection, transaction, note, ct).ConfigureAwait(false);
            }
        }, ct);
    }

    public Task RemoveAsync(VaultPath path, CancellationToken ct) =>
        WriteAsync((connection, transaction) => DeleteFileAsync(connection, transaction, path, ct), ct);

    public Task RenameAsync(VaultPath from, VaultPath to, CancellationToken ct) =>
        WriteAsync(async (connection, transaction) =>
        {
            var id = await IdOfAsync(connection, transaction, from, ct).ConfigureAwait(false);
            if (id is null)
            {
                return;
            }

            await DeleteFileAsync(connection, transaction, to, ct).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                UPDATE files SET path = $path, path_fold = $fold, name = $name, ext = $ext, kind = $kind WHERE id = $id;
                UPDATE content_fts SET path = $searchPath, name = $searchName WHERE rowid = $id;
                """, ct, ("$path", to.ToString()), ("$fold", to.CaseFoldKey), ("$name", NameOf(to)), ("$ext", to.Extension),
                ("$kind", (int)FileClassifier.Classify(to)), ("$id", id.Value),
                ("$searchPath", FoldedText.Fold(to.ToString())), ("$searchName", FoldedText.Fold(to.Name))).ConfigureAwait(false);
        }, ct);

    public async Task<IndexSnapshotInfo> GetSnapshotInfoAsync(CancellationToken ct)
    {
        await using var connection = await ConnectAsync(ct).ConfigureAwait(false);
        var version = 0;
        await using (var command = Command(connection, null, "SELECT value FROM meta WHERE key = 'analyzer_version'"))
        {
            if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string text)
            {
                version = int.Parse(text, CultureInfo.InvariantCulture);
            }
        }

        var entries = new Dictionary<VaultPath, IndexStamp>();
        await using (var command = Command(connection, null, "SELECT path, size, mtime_utc, sha FROM files"))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                entries[VaultPath.Create(reader.GetString(0)).Value] = Stamp(reader, 1);
            }
        }

        return new IndexSnapshotInfo(version, entries);
    }

    public Task ResetAsync(int analyzerVersion, CancellationToken ct) =>
        WriteAsync((connection, transaction) => ExecuteAsync(connection, transaction, """
            DELETE FROM files;
            DELETE FROM content_fts;
            INSERT INTO meta(key, value) VALUES ('analyzer_version', $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """, ct, ("$v", analyzerVersion.ToString(CultureInfo.InvariantCulture))), ct);

    public async Task<IReadOnlyList<IndexedNoteSummary>> GetAllSummariesAsync(CancellationToken ct)
    {
        await using var connection = await ConnectAsync(ct).ConfigureAwait(false);
        var rows = await ReadFilesAsync(connection, null, ct).ConfigureAwait(false);
        var details = await ReadDetailsAsync(connection, null, ct).ConfigureAwait(false);
        return [.. rows.Select(r => new IndexedNoteSummary(r.Path, r.Kind, r.Stamp, Metadata(r, details.For(r.Id))))];
    }

    public async Task<IndexedNote?> GetAsync(VaultPath path, CancellationToken ct)
    {
        await using var connection = await ConnectAsync(ct).ConfigureAwait(false);
        var row = (await ReadFilesAsync(connection, path, ct).ConfigureAwait(false)).FirstOrDefault();
        if (row is null)
        {
            return null;
        }

        var details = await ReadDetailsAsync(connection, row.Id, ct).ConfigureAwait(false);
        string? text = null;
        if (row.HasText)
        {
            await using var command = Command(connection, null, "SELECT body FROM contents WHERE file_id = $id", ("$id", row.Id));
            text = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string ?? string.Empty;
        }

        return new IndexedNote(row.Path, row.Kind, row.Stamp, text, Metadata(row, details.For(row.Id)));
    }

    /// <summary>Pre-filters with <see cref="FtsQueryBuilder"/> and evaluates the candidates with the domain matcher.</summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, SearchOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);
        if (query.IsEmpty)
        {
            return [];
        }

        var builder = new FtsQueryBuilder();
        var condition = builder.Build(query);
        var (low, high) = await IdRangeAsync(ct).ConfigureAwait(false);
        if (high < low)
        {
            return [];
        }

        IReadOnlyDictionary<long, FileDetails>? details = null;
        if (NeedsMetadata(query.Root!))
        {
            await using var connection = await ConnectAsync(ct).ConfigureAwait(false);
            details = (await ReadDetailsAsync(connection, null, ct).ConfigureAwait(false)).All;
        }

        // Reading the candidates' text and matching it dominate: each worker reads one slice of ids on its own
        // connection (WAL allows concurrent readers) and matches it with its own matcher.
        var slices = Math.Clamp(Environment.ProcessorCount, 1, 8);
        var width = ((high - low) / slices) + 1;
        var hits = new ConcurrentBag<SearchHit>();
        await Parallel.ForEachAsync(Enumerable.Range(0, slices), new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = slices }, async (slice, token) =>
        {
            var from = low + (slice * width);
            var to = Math.Min(high, from + width - 1);
            if (from > high)
            {
                return;
            }

            await using var connection = await ConnectAsync(token).ConfigureAwait(false);
            var range = $"f.id BETWEEN {from.ToString(CultureInfo.InvariantCulture)} AND {to.ToString(CultureInfo.InvariantCulture)}";
            var candidates = await ReadFilesAsync(connection, condition is null ? range : $"{range} AND {condition}", builder.Parameters, includeBody: true, token).ConfigureAwait(false);
            var matcher = NoteMatcher.Create(query, options.MatchCase);
            foreach (var row in candidates)
            {
                token.ThrowIfCancellationRequested();
                var metadata = details is null
                    ? NoteMetadata.Empty with { WordCount = row.WordCount, Frontmatter = row.Frontmatter }
                    : Metadata(row, details.GetValueOrDefault(row.Id) ?? new FileDetails());
                if (matcher.Match(new SearchableNote(row.Path, row.Body ?? string.Empty, metadata)) is { } match)
                {
                    hits.Add(new SearchHit(row.Path, row.Kind, row.Stamp.LastWriteUtc, match.Ranges, SearchRanking.Score(match)));
                }
            }
        }).ConfigureAwait(false);

        return SearchRanking.Order(hits, options);
    }

    private async Task<(long Low, long High)> IdRangeAsync(CancellationToken ct)
    {
        await using var connection = await ConnectAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, null, "SELECT coalesce(min(id), 1), coalesce(max(id), 0) FROM files");
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>Whether evaluating the query reads tags, headings or tasks (frontmatter comes with the file row).</summary>
    private static bool NeedsMetadata(SearchNode node) => node switch
    {
        FieldNode { Field: SearchField.Tag or SearchField.Section or SearchField.Task or SearchField.TaskTodo or SearchField.TaskDone } => true,
        FieldNode field => NeedsMetadata(field.Operand),
        CompositeNode composite => composite.Operands.Any(NeedsMetadata),
        NotNode not => NeedsMetadata(not.Operand),
        GroupNode group => NeedsMetadata(group.Inner),
        _ => false,
    };

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private async Task<bool> TryInitializeAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = await ConnectAsync(ct).ConfigureAwait(false);
            await using (var check = Command(connection, null, "PRAGMA quick_check"))
            {
                if (await check.ExecuteScalarAsync(ct).ConfigureAwait(false) is not "ok")
                {
                    return false;
                }
            }

            await ExecuteAsync(connection, null, IndexSchema.Create, ct).ConfigureAwait(false);
            await using var version = Command(connection, null, "SELECT value FROM meta WHERE key = 'schema_version'");
            var stored = await version.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            if (stored is null)
            {
                await ExecuteAsync(connection, null, "INSERT INTO meta(key, value) VALUES ('schema_version', $v)", ct, ("$v", IndexSchema.CurrentVersion.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
                return true;
            }

            // Only one schema version exists so far; a different one (e.g. from a newer app) is discarded.
            return int.Parse(stored, CultureInfo.InvariantCulture) == IndexSchema.CurrentVersion;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private async Task<SqliteConnection> ConnectAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // Searches read the text of thousands of candidates: map the file and keep a 64 MB page cache per connection.
        await ExecuteAsync(connection, null, "PRAGMA mmap_size = 268435456; PRAGMA cache_size = -65536;", ct).ConfigureAwait(false);
        return connection;
    }

    private async Task WriteAsync(Func<SqliteConnection, SqliteTransaction, Task> work, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await ConnectAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await work(connection, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task<long?> IdOfAsync(SqliteConnection connection, SqliteTransaction? transaction, VaultPath path, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT id FROM files WHERE path = $path", ("$path", path.ToString()));
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as long?;
    }

    private static async Task DeleteFileAsync(SqliteConnection connection, SqliteTransaction transaction, VaultPath path, CancellationToken ct)
    {
        var id = await IdOfAsync(connection, transaction, path, ct).ConfigureAwait(false);
        if (id is not null)
        {
            await ExecuteAsync(connection, transaction, "DELETE FROM content_fts WHERE rowid = $id; DELETE FROM files WHERE id = $id;", ct, ("$id", id.Value)).ConfigureAwait(false);
        }
    }

    private static async Task InsertFileAsync(SqliteConnection connection, SqliteTransaction transaction, IndexedNote note, CancellationToken ct)
    {
        var m = note.Metadata;
        var fm = m.Frontmatter;
        await using var insert = Command(connection, transaction, """
            INSERT INTO files(path, path_fold, name, ext, kind, size, mtime_utc, sha, word_count, has_text, fm_json, fm_start, fm_length, fm_line, fm_error)
            VALUES ($path, $fold, $name, $ext, $kind, $size, $mtime, $sha, $words, $hasText, $fmJson, $fmStart, $fmLength, $fmLine, $fmError);
            SELECT last_insert_rowid();
            """,
            ("$path", note.Path.ToString()), ("$fold", note.Path.CaseFoldKey), ("$name", NameOf(note.Path)), ("$ext", note.Path.Extension),
            ("$kind", (int)note.Kind), ("$size", note.Stamp.Size), ("$mtime", note.Stamp.LastWriteUtc.UtcTicks), ("$sha", note.Stamp.Sha?.ToString()),
            ("$words", m.WordCount), ("$hasText", note.Text is null ? 0 : 1),
            ("$fmJson", fm is null ? null : StructuredValueJson.Serialize(fm.Values)), ("$fmStart", fm?.Span.Start), ("$fmLength", fm?.Span.Length),
            ("$fmLine", fm?.Span.Line), ("$fmError", fm?.Error));
        var id = (long)(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

        if (note.Text is not null)
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO contents(file_id, body) VALUES ($id, $body)", ct, ("$id", id), ("$body", note.Text)).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction, "INSERT INTO content_fts(rowid, path, name, body) VALUES ($id, $path, $name, $body)", ct,
            ("$id", id), ("$path", FoldedText.Fold(note.Path.ToString())), ("$name", FoldedText.Fold(note.Path.Name)),
            ("$body", FoldedText.Fold(note.Text ?? string.Empty))).ConfigureAwait(false);

        var ord = 0;
        foreach (var link in m.Links.Concat(m.Embeds))
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO links(source_id, ord, kind, raw_target, subpath, subpath_kind, alias, line, col, start, length, target_line, target_col, target_start, target_length)
                VALUES ($id, $ord, $kind, $target, $subpath, $subpathKind, $alias, $line, $col, $start, $length, $tline, $tcol, $tstart, $tlength)
                """, ct, ("$id", id), ("$ord", ord++), ("$kind", (int)link.Kind), ("$target", link.RawTarget), ("$subpath", link.Subpath),
                ("$subpathKind", (int)link.SubpathKind), ("$alias", link.Alias), ("$line", link.Span.Line), ("$col", link.Span.Column),
                ("$start", link.Span.Start), ("$length", link.Span.Length), ("$tline", link.TargetSpan.Line), ("$tcol", link.TargetSpan.Column),
                ("$tstart", link.TargetSpan.Start), ("$tlength", link.TargetSpan.Length)).ConfigureAwait(false);
        }

        ord = 0;
        foreach (var tag in m.Tags)
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO tags(file_id, ord, tag, tag_fold, tag_search, line, source) VALUES ($id, $ord, $tag, $fold, $search, $line, $source)", ct,
                ("$id", id), ("$ord", ord++), ("$tag", tag.Tag.Name), ("$fold", tag.Tag.FoldKey), ("$search", FoldedText.Fold(tag.Tag.ToString().TrimStart('#'))),
                ("$line", tag.Line), ("$source", (int)tag.Source)).ConfigureAwait(false);
        }

        ord = 0;
        foreach (var heading in m.Headings)
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO headings(file_id, ord, level, text, line) VALUES ($id, $ord, $level, $text, $line)", ct,
                ("$id", id), ("$ord", ord++), ("$level", heading.Level), ("$text", heading.Text), ("$line", heading.Line)).ConfigureAwait(false);
        }

        ord = 0;
        foreach (var block in m.Blocks)
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO blocks(file_id, ord, block_id, line) VALUES ($id, $ord, $block, $line)", ct,
                ("$id", id), ("$ord", ord++), ("$block", block.Id.Value), ("$line", block.Line)).ConfigureAwait(false);
        }

        ord = 0;
        foreach (var alias in m.Aliases)
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO aliases(file_id, ord, alias) VALUES ($id, $ord, $alias)", ct,
                ("$id", id), ("$ord", ord++), ("$alias", alias)).ConfigureAwait(false);
        }

        ord = 0;
        foreach (var task in m.Tasks)
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO tasks(file_id, ord, line, done, status, text) VALUES ($id, $ord, $line, $done, $status, $text)", ct,
                ("$id", id), ("$ord", ord++), ("$line", task.Line), ("$done", task.IsDone ? 1 : 0), ("$status", task.Status.ToString()), ("$text", task.Text)).ConfigureAwait(false);
        }

        foreach (var (key, value) in fm?.Values.Entries ?? [])
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO properties(file_id, key, key_fold, value_json, value_text) VALUES ($id, $key, $fold, $json, $text)", ct,
                ("$id", id), ("$key", key), ("$fold", key.ToUpperInvariant()), ("$json", StructuredValueJson.Serialize(value)), ("$text", StructuredValueJson.Text(value))).ConfigureAwait(false);
        }
    }

    private static Task<List<FileRow>> ReadFilesAsync(SqliteConnection connection, VaultPath? path, CancellationToken ct) =>
        path is null
            ? ReadFilesAsync(connection, null, [], includeBody: false, ct)
            : ReadFilesAsync(connection, "f.path = $path", [("$path", path.Value.ToString())], includeBody: false, ct);

    private static async Task<List<FileRow>> ReadFilesAsync(
        SqliteConnection connection, string? condition, IReadOnlyList<(string Name, object Value)> parameters, bool includeBody, CancellationToken ct)
    {
        var sql = "SELECT f.id, f.path, f.kind, f.size, f.mtime_utc, f.sha, f.word_count, f.has_text, f.fm_json, f.fm_start, f.fm_length, f.fm_line, f.fm_error"
            + (includeBody ? ", c.body FROM files f LEFT JOIN contents c ON c.file_id = f.id" : " FROM files f")
            + (condition is null ? string.Empty : " WHERE " + condition);
        await using var command = Command(connection, null, sql, [.. parameters.Select(p => (p.Name, (object?)p.Value))]);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<FileRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            Frontmatter? frontmatter = null;
            if (!reader.IsDBNull(9))
            {
                var values = reader.IsDBNull(8) ? StructuredValue.MapValue.Of() : (StructuredValue.MapValue)StructuredValueJson.Deserialize(reader.GetString(8));
                frontmatter = new Frontmatter(values, new SourceSpan(reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11), 0), reader.IsDBNull(12) ? null : reader.GetString(12));
            }

            rows.Add(new FileRow(
                reader.GetInt64(0),
                VaultPath.Create(reader.GetString(1)).Value,
                (FileKind)reader.GetInt32(2),
                Stamp(reader, 3),
                reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                reader.GetInt32(7) == 1,
                frontmatter,
                includeBody && !reader.IsDBNull(13) ? reader.GetString(13) : null));
        }

        return rows;
    }

    private static async Task<Details> ReadDetailsAsync(SqliteConnection connection, long? fileId, CancellationToken ct)
    {
        var details = new Details();
        var filter = fileId is null ? string.Empty : " WHERE {0} = $id";

        await ReadAsync("SELECT source_id, kind, raw_target, subpath, subpath_kind, alias, start, length, line, col, target_start, target_length, target_line, target_col FROM links"
            + string.Format(CultureInfo.InvariantCulture, filter, "source_id") + " ORDER BY source_id, ord", r =>
            details.Get(r.GetInt64(0)).Links.Add(new Link(
                (LinkKind)r.GetInt32(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), (SubpathKind)r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5),
                new SourceSpan(r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9)),
                new SourceSpan(r.GetInt32(10), r.GetInt32(11), r.GetInt32(12), r.GetInt32(13)))));
        await ReadAsync("SELECT file_id, tag, source, line FROM tags" + string.Format(CultureInfo.InvariantCulture, filter, "file_id") + " ORDER BY file_id, ord", r =>
            details.Get(r.GetInt64(0)).Tags.Add(new TagOccurrence(Tag.Create(r.GetString(1)).Value, (TagSource)r.GetInt32(2), r.IsDBNull(3) ? 0 : r.GetInt32(3))));
        await ReadAsync("SELECT file_id, level, text, line FROM headings" + string.Format(CultureInfo.InvariantCulture, filter, "file_id") + " ORDER BY file_id, ord", r =>
            details.Get(r.GetInt64(0)).Headings.Add(Heading.Create(r.GetInt32(1), r.GetString(2), r.GetInt32(3)).Value));
        await ReadAsync("SELECT file_id, block_id, line FROM blocks" + string.Format(CultureInfo.InvariantCulture, filter, "file_id") + " ORDER BY file_id, ord", r =>
            details.Get(r.GetInt64(0)).Blocks.Add(new BlockReference(BlockId.Create(r.GetString(1)).Value, r.GetInt32(2))));
        await ReadAsync("SELECT file_id, alias FROM aliases" + string.Format(CultureInfo.InvariantCulture, filter, "file_id") + " ORDER BY file_id, ord", r =>
            details.Get(r.GetInt64(0)).Aliases.Add(r.GetString(1)));
        await ReadAsync("SELECT file_id, line, status, text FROM tasks" + string.Format(CultureInfo.InvariantCulture, filter, "file_id") + " ORDER BY file_id, ord", r =>
            details.Get(r.GetInt64(0)).Tasks.Add(new TaskItem(r.GetInt32(1), r.GetString(2)[0], r.GetString(3))));
        return details;

        async Task ReadAsync(string sql, Action<SqliteDataReader> row)
        {
            await using var command = fileId is null ? Command(connection, null, sql) : Command(connection, null, sql, ("$id", fileId.Value));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                row(reader);
            }
        }
    }

    private static NoteMetadata Metadata(FileRow row, FileDetails details) => new(
        row.Frontmatter,
        details.Headings,
        [.. details.Links.Where(l => !l.IsEmbed)],
        [.. details.Links.Where(l => l.IsEmbed)],
        details.Tags,
        details.Blocks,
        details.Tasks,
        details.Aliases,
        row.WordCount);

    private static IndexStamp Stamp(SqliteDataReader reader, int offset) => new(
        reader.GetInt64(offset),
        new DateTimeOffset(reader.GetInt64(offset + 1), TimeSpan.Zero),
        reader.IsDBNull(offset + 2) ? null : BlobSha.Parse(reader.GetString(offset + 2)).Value);

    private static string NameOf(VaultPath path) => path.Extension == "md" ? path.NameWithoutExtension : path.Name;

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
#pragma warning disable CA2100 // SQL text is constant; values always go through parameters.
        var command = new SqliteCommand(sql, connection, transaction);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private sealed record FileRow(long Id, VaultPath Path, FileKind Kind, IndexStamp Stamp, int WordCount, bool HasText, Frontmatter? Frontmatter, string? Body = null);

    private sealed class FileDetails
    {
        public List<Link> Links { get; } = [];

        public List<TagOccurrence> Tags { get; } = [];

        public List<Heading> Headings { get; } = [];

        public List<BlockReference> Blocks { get; } = [];

        public List<string> Aliases { get; } = [];

        public List<TaskItem> Tasks { get; } = [];
    }

    private sealed class Details
    {
        private static readonly FileDetails Empty = new();
        private readonly Dictionary<long, FileDetails> _byFile = [];

        public FileDetails Get(long id)
        {
            if (!_byFile.TryGetValue(id, out var details))
            {
                details = new FileDetails();
                _byFile[id] = details;
            }

            return details;
        }

        public FileDetails For(long id) => _byFile.GetValueOrDefault(id) ?? Empty;

        public IReadOnlyDictionary<long, FileDetails> All => _byFile;
    }
}
