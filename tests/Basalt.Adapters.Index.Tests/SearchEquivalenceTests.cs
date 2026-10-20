using System.Text;
using Basalt.Adapters.Markdown;
using Basalt.Application.Ports.Index;
using Basalt.Application.Ports.Markdown;
using Basalt.Domain.Notes;
using Basalt.Domain.Search;
using Basalt.Domain.Sync;
using Basalt.Testing;
using Basalt.Testing.Fakes;
using Basalt.Testing.Generators;
using FsCheck;
using FsCheck.Fluent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Basalt.Adapters.Index.Tests;

/// <summary>
/// Spec 08 §4.3: for any corpus and query, <see cref="SqliteNoteIndex"/> returns exactly what the linear
/// <see cref="InMemoryNoteIndex"/> oracle returns (same files, ranges and order).
/// </summary>
public sealed class SearchEquivalenceTests : IAsyncLifetime
{
    private static readonly string[] Words =
    [
        "reunión", "Reunion", "cliente", "café", "CAFÉ", "cafe", "proyecto", "pro", "ab", "x", "naïve", "Straße", "日本語",
        "emoji😀", "fecha-límite", "2026-10-19", "or", "tag", "Café", "ÅNGSTRÖM", "İstanbul", "ﬁn",
    ];

    private static readonly string[] Regexes = ["/caf[eé]/", "/\\d{4}-\\d{2}/", "/^#/", "/pro.*o/", "/(?m)^- \\[x\\]/", "/ab|x/"];

    private static readonly IMarkdownAnalyzer Analyzer =
        new ServiceCollection().AddBasaltMarkdownAdapter().BuildServiceProvider().GetRequiredService<IMarkdownAnalyzer>();

    private readonly TempDirectory _directory = TempDirectory.Create("equivalence");
    private SqliteNoteIndex _sqlite = null!;

    public async ValueTask InitializeAsync() => _sqlite = await SqliteNoteIndex.OpenAsync(_directory.Combine("index.db"), TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _sqlite.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await _directory.DisposeAsync();
    }

    [BasaltProperty(MaxTest = 60)]
    [Trait("Spec", "PERS-4.3")]
    public Property Sqlite_And_Oracle_ReturnTheSameHits() =>
        Prop.ForAll(Corpus().ToArbitrary(), Gen.ListOf(Query(), 8).ToArbitrary(), Gen.Elements(false, true).ToArbitrary(), (corpus, queries, matchCase) =>
            CompareAsync(corpus, queries, matchCase).GetAwaiter().GetResult());

    private async Task<bool> CompareAsync(IReadOnlyList<IndexedNote> corpus, IReadOnlyList<string> queries, bool matchCase)
    {
        var ct = TestContext.Current.CancellationToken;
        var oracle = new InMemoryNoteIndex();
        await _sqlite.ResetAsync(1, ct);
        await _sqlite.UpsertManyAsync(corpus, ct);
        await oracle.UpsertManyAsync(corpus, ct);

        foreach (var text in queries)
        {
            var parsed = SearchQueryParser.Parse(text);
            if (parsed.IsFailure)
            {
                continue;
            }

            var options = new SearchOptions(MatchCase: matchCase);
            var expected = await oracle.SearchAsync(parsed.Value, options, ct);
            var actual = await _sqlite.SearchAsync(parsed.Value, options, ct);
            if (!expected.Select(Describe).SequenceEqual(actual.Select(Describe)))
            {
                throw new InvalidOperationException(
                    $"Query «{text}» (matchCase={matchCase}) differs.\nOracle: {string.Join(" | ", expected.Select(Describe))}\nSQLite: {string.Join(" | ", actual.Select(Describe))}");
            }
        }

        return true;
    }

    private static string Describe(SearchHit hit) => $"{hit.Path} {hit.Score} [{string.Join(",", hit.Ranges.Select(r => $"{r.Target}:{r.Start}+{r.Length}"))}]";

    private static Gen<string> Word => Gen.Elements(Words);

    private static Gen<IReadOnlyList<IndexedNote>> Corpus() =>
        Gen.ListOf(NoteGen(), 12).Select(notes => (IReadOnlyList<IndexedNote>)[.. notes.DistinctBy(n => n.Path.CaseFoldKey)]);

    private static Gen<IndexedNote> NoteGen() =>
        from folder in Gen.Elements("", "Proyectos/", "Archivo/", "Café/", "日本/")
        from name in Word
        from attachment in Gen.Elements(false, false, false, true)
        from lines in Gen.ListOf(Line(), 6)
        from frontmatter in Gen.Elements(false, true)
        from status in Word
        from tag in Word
        let path = VaultPath.Create(folder + name.Replace("/", "-", StringComparison.Ordinal) + (attachment ? ".png" : ".md")).Value
        let text = attachment ? null : (frontmatter ? $"---\nstatus: {status}\ntags: [{Slug(tag)}]\n---\n" : string.Empty) + string.Join("\n", lines)
        select Indexed(path, text);

    private static Gen<string> Line() => Gen.OneOf(
        Gen.ListOf(Word, 4).Select(ws => string.Join(' ', ws)),
        Word.Select(w => "#" + Slug(w) + " " + w),
        Word.Select(w => "# " + w),
        Word.Select(w => "- [ ] " + w),
        Word.Select(w => "- [x] " + w),
        Gen.Constant(string.Empty));

    private static string Slug(string word)
    {
        var builder = new StringBuilder();
        foreach (var c in word)
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : '-');
        }

        return builder.ToString().Trim('-') is { Length: > 0 } slug && !slug.All(char.IsDigit) ? slug : "t";
    }

    private static IndexedNote Indexed(VaultPath path, string? text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        return new IndexedNote(
            path,
            FileClassifier.Classify(path),
            new IndexStamp(bytes.Length, new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero), GitBlobHasher.Compute(bytes)),
            text,
            text is null ? Domain.Metadata.NoteMetadata.Empty : Analyzer.Analyze(text));
    }

    private static Gen<string> Query() => Expression(3);

    private static Gen<string> Expression(int depth)
    {
        var atom = Gen.OneOf(
            Word,
            Gen.Two(Word).Select(t => $"\"{t.Item1} {t.Item2}\""),
            Gen.Elements(Regexes),
            from field in Gen.Elements("file", "path", "content", "task", "task-todo", "task-done")
            from w in Word
            select $"{field}:{w}",
            Word.Select(w => "tag:#" + Slug(w)),
            Word.Select(w => "tag:" + Slug(w)),
            from field in Gen.Elements("line", "block", "section")
            from a in Word
            from b in Word
            select $"{field}:({a} {b})",
            Gen.Elements("[status]", "[tags]", "[status:null]", "[nada]"),
            Word.Select(w => $"[status:{w}]"));
        if (depth == 0)
        {
            return atom;
        }

        var inner = Expression(depth - 1);
        return Gen.OneOf(
            atom,
            atom,
            Gen.Two(inner).Select(t => $"{t.Item1} {t.Item2}"),
            Gen.Two(inner).Select(t => $"{t.Item1} OR {t.Item2}"),
            inner.Select(e => "-" + e),
            inner.Select(e => $"({e})"),
            Gen.Two(inner).Select(t => $"line:({t.Item1} {t.Item2})"));
    }
}
