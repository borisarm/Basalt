using FsCheck;
using FsCheck.Fluent;

namespace Basalt.Testing.Generators;

/// <summary>A random Markdown document with wikilinks plus a random set of renames over a small vault.</summary>
public sealed record RewriteScenario(string Source, string Document, IReadOnlyList<string> Files, IReadOnlyDictionary<string, string> Renames)
{
    private static readonly string[] Universe = ["A.md", "B.md", "x/Nota.md", "y/Nota.md", "Dir/C.md", "img.png", "Dir/Src.md", "Src.md"];
    private static readonly string[] Destinations = ["New.md", "z/Nota.md", "Dir2/C.md", "B2.md", "x/Moved.md", "pics/img.png", "Dir/Nota.md", "Nota.md"];
    private static readonly string[] Targets = ["A", "B", "Nota", "x/Nota", "y/Nota", "Dir/C", "C", "img.png", "Missing", "./C", "../A", "B.md", "dir/c"];
    private static readonly string[] Texts = ["hola ", "\n", "texto # y | ", "`x` ", "ñandú ", "[no link] ", "]] ", "😀 "];

    public static Gen<RewriteScenario> Generator() =>
        from source in Gen.Elements("Dir/Src.md", "Src.md")
        from fragments in Gen.ListOf(Fragment(), 16)
        from renameCount in Gen.Choose(0, 4)
        from renamed in Gen.Shuffle(Universe.Where(f => f != source).ToArray()).Select(f => f.Take(renameCount).ToArray())
        from destinations in Gen.Shuffle(Destinations)
        select Build(source, string.Concat(fragments), renamed, destinations);

    public static Arbitrary<RewriteScenario> Arbitrary() => Generator().ToArbitrary();

    private static Gen<string> Fragment() =>
        Gen.Frequency(
            (3, Gen.Elements(Texts)),
            (2, from target in Gen.Elements(Targets)
                from subpath in Gen.Elements(string.Empty, "#H", "#^b1")
                from alias in Gen.Elements(string.Empty, "|alias", "| a b ")
                from embed in Gen.Elements(string.Empty, "!")
                select $"{embed}[[{target}{subpath}{alias}]]"));

    private static RewriteScenario Build(string source, string document, string[] renamed, string[] destinations)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        var occupied = new HashSet<string>(Universe, StringComparer.OrdinalIgnoreCase);
        var free = destinations.Where(d => !occupied.Contains(d)).ToList();
        foreach (var from in renamed)
        {
            var to = free.FirstOrDefault(d => d.EndsWith(Path.GetExtension(from), StringComparison.Ordinal));
            if (to is null)
            {
                continue;
            }

            free.Remove(to);
            renames[from] = to;
        }

        return new RewriteScenario(source, document, Universe, renames);
    }

    public override string ToString() =>
        $"Source={Source} Renames={string.Join(", ", Renames.Select(kv => $"{kv.Key}->{kv.Value}"))} Document={Document.Replace("\n", "\\n", StringComparison.Ordinal)}";
}
