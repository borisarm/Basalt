namespace Basalt.Adapters.Index;

/// <summary>SQLite schema of the vault index (spec 08 §2.1) and its migrations.</summary>
internal static class IndexSchema
{
    public const int CurrentVersion = 1;

    /// <summary>Version 1. Beyond 08 §2.1: link target spans/subpath kind, tag source order, task status and the
    /// frontmatter block so that <c>NoteMetadata</c> is rebuilt exactly.</summary>
    public const string Create = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS files (
          id INTEGER PRIMARY KEY,
          path TEXT NOT NULL UNIQUE,
          path_fold TEXT NOT NULL,
          name TEXT NOT NULL,
          ext TEXT NOT NULL,
          kind INTEGER NOT NULL,
          size INTEGER NOT NULL,
          mtime_utc INTEGER NOT NULL,
          ctime_utc INTEGER,
          sha TEXT,
          word_count INTEGER,
          has_text INTEGER NOT NULL,
          fm_json TEXT,
          fm_start INTEGER, fm_length INTEGER, fm_line INTEGER, fm_error TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_files_name ON files(name COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS ix_files_path_fold ON files(path_fold);

        CREATE TABLE IF NOT EXISTS links (
          source_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
          ord INTEGER NOT NULL,
          kind INTEGER NOT NULL,
          raw_target TEXT NOT NULL,
          resolved_id INTEGER REFERENCES files(id) ON DELETE SET NULL,
          subpath TEXT, subpath_kind INTEGER NOT NULL, alias TEXT,
          line INTEGER NOT NULL, col INTEGER NOT NULL, start INTEGER NOT NULL, length INTEGER NOT NULL,
          target_line INTEGER NOT NULL, target_col INTEGER NOT NULL, target_start INTEGER NOT NULL, target_length INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_links_source ON links(source_id);
        CREATE INDEX IF NOT EXISTS ix_links_resolved ON links(resolved_id);
        CREATE INDEX IF NOT EXISTS ix_links_raw ON links(raw_target COLLATE NOCASE);

        CREATE TABLE IF NOT EXISTS tags (file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                                         ord INTEGER NOT NULL, tag TEXT NOT NULL, tag_fold TEXT NOT NULL, line INTEGER, source INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_tags_fold ON tags(tag_fold);
        CREATE INDEX IF NOT EXISTS ix_tags_file ON tags(file_id);

        CREATE TABLE IF NOT EXISTS headings (file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                                             ord INTEGER NOT NULL, level INTEGER NOT NULL, text TEXT NOT NULL, line INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_headings_file ON headings(file_id);
        CREATE TABLE IF NOT EXISTS blocks (file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                                           ord INTEGER NOT NULL, block_id TEXT NOT NULL, line INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_blocks_file ON blocks(file_id);
        CREATE TABLE IF NOT EXISTS aliases (file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                                            ord INTEGER NOT NULL, alias TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_aliases_file ON aliases(file_id);
        CREATE TABLE IF NOT EXISTS properties (file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                                               key TEXT NOT NULL, key_fold TEXT NOT NULL, value_json TEXT NOT NULL, value_text TEXT);
        CREATE INDEX IF NOT EXISTS ix_properties_file ON properties(file_id);
        CREATE INDEX IF NOT EXISTS ix_properties_key ON properties(key_fold);
        CREATE TABLE IF NOT EXISTS tasks (file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                                          ord INTEGER NOT NULL, line INTEGER NOT NULL, done INTEGER NOT NULL, status TEXT NOT NULL, text TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_tasks_file ON tasks(file_id);

        CREATE VIRTUAL TABLE IF NOT EXISTS content_fts USING fts5(
          path, name, body, tokenize = 'unicode61 remove_diacritics 2', prefix = '2 3'
        );
        """;
}
