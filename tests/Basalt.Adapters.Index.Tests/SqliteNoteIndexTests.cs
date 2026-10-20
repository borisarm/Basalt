using Basalt.Application.Ports.Index;
using Basalt.Testing;
using Basalt.Testing.Contracts;
using Microsoft.Data.Sqlite;

namespace Basalt.Adapters.Index.Tests;

public sealed class SqliteNoteIndexTests : NoteIndexContract
{
    private readonly TempDirectory _directory = TempDirectory.Create("index");

    private string DbPath => _directory.Combine("index.db");

    protected override async Task<INoteIndex> CreateSutAsync() => await SqliteNoteIndex.OpenAsync(DbPath, Ct);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await _directory.DisposeAsync();
    }

    [Fact]
    public async Task Data_PersistsAcrossReopen()
    {
        await Sut.UpsertAsync(Note("a.md", "persisted", RichMetadata()), Ct);
        await Sut.ResetAsync(3, Ct);
        await Sut.UpsertAsync(Note("a.md", "persisted", RichMetadata()), Ct);
        await Sut.DisposeAsync();

        await using var reopened = await SqliteNoteIndex.OpenAsync(DbPath, Ct);

        (await reopened.GetAsync(P("a.md"), Ct))!.Text.Should().Be("persisted");
        (await reopened.GetSnapshotInfoAsync(Ct)).AnalyzerVersion.Should().Be(3);
        reopened.WasRecreated.Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "IDX-2.2")]
    public async Task CorruptDatabase_IsRecreatedEmpty()
    {
        await Sut.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await File.WriteAllBytesAsync(DbPath, [.. "this is not a sqlite database at all"u8, .. new byte[4096]], Ct);

        await using var index = await SqliteNoteIndex.OpenAsync(DbPath, Ct);

        index.WasRecreated.Should().BeTrue();
        (await index.GetAllSummariesAsync(Ct)).Should().BeEmpty();
        await index.UpsertAsync(Note("a.md", "works again"), Ct);
        (await index.GetAsync(P("a.md"), Ct))!.Text.Should().Be("works again");
    }

    [Fact]
    public async Task NewerSchemaVersion_IsRecreated()
    {
        await Sut.UpsertAsync(Note("a.md", "x"), Ct);
        await using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            await connection.OpenAsync(Ct);
            await using var command = new SqliteCommand("UPDATE meta SET value = '99' WHERE key = 'schema_version'", connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await Sut.DisposeAsync();

        await using var index = await SqliteNoteIndex.OpenAsync(DbPath, Ct);

        index.WasRecreated.Should().BeTrue();
        (await index.GetAllSummariesAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentReadsAndWrites_AreSafe()
    {
        var writers = Enumerable.Range(0, 50).Select(i => Sut.UpsertAsync(Note($"n{i}.md", $"text {i}", RichMetadata()), Ct));
        var readers = Enumerable.Range(0, 50).Select(async _ => (await Sut.GetAllSummariesAsync(Ct)).Count);

        await Task.WhenAll(writers.Concat<Task>(readers));

        (await Sut.GetAllSummariesAsync(Ct)).Should().HaveCount(50);
    }

    [Fact]
    public async Task Rename_OntoAnExistingPath_ReplacesIt()
    {
        await Sut.UpsertAsync(Note("a.md", "A"), Ct);
        await Sut.UpsertAsync(Note("b.md", "B"), Ct);

        await Sut.RenameAsync(P("a.md"), P("b.md"), Ct);

        (await Sut.GetAsync(P("b.md"), Ct))!.Text.Should().Be("A");
        (await Sut.GetAllSummariesAsync(Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Properties_AreStoredForSearch()
    {
        await Sut.UpsertAsync(Note("a.md", "x", RichMetadata()), Ct);

        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync(Ct);
        await using var command = new SqliteCommand("SELECT key, value_text FROM properties ORDER BY key", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add($"{reader.GetString(0)}={(reader.IsDBNull(1) ? "<null>" : reader.GetString(1))}");
        }

        rows.Should().Equal("done=false", "due=2026-10-19", "meta=<null>", "rating=4.5", "status=draft", "tags=a");
    }
}
