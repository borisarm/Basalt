using System.Globalization;
using System.Text;

namespace Basalt.VaultGenerator;

/// <summary>Size of the generated vault. Defaults are the reference vault of spec 11.</summary>
public sealed record VaultGeneratorOptions(
    int Seed = 42,
    int Notes = 10_000,
    int Attachments = 1_000,
    int Folders = 300,
    int MaxDepth = 4);

/// <summary>
/// Deterministic reference vault (spec 11): notes of ~2 KB on average (a few up to 200 KB) with ~5 links and ~2 tags
/// each, frontmatter, headings, tasks and block ids; attachments of 50–500 KB; folders up to <c>MaxDepth</c> levels.
/// The same options always produce byte-identical files.
/// </summary>
public static class ReferenceVaultGenerator
{
    private static readonly string[] Words =
    [
        "nota", "idea", "proyecto", "reunión", "cliente", "plan", "tarea", "revisión", "diseño", "datos", "informe", "equipo",
        "sync", "vault", "enlace", "grafo", "búsqueda", "índice", "markdown", "obsidian", "basalt", "github", "lunes",
        "martes", "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit", "sed", "do", "eiusmod",
        "tempor", "incididunt", "labore", "magna", "aliqua", "ut", "enim", "minim", "veniam", "quis", "nostrud",
    ];

    private static readonly string[] TagNames = ["trabajo", "personal", "idea", "lectura", "trabajo/cliente-a", "trabajo/cliente-b", "proyecto/x", "pendiente", "diario", "referencia"];

    /// <summary>Returns the relative paths written.</summary>
    public static IReadOnlyList<string> Generate(string root, VaultGeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);
#pragma warning disable CA5394 // deterministic, non-cryptographic test data
        var random = new Random(options.Seed);
        var folders = Folders(random, options);
        var titles = Enumerable.Range(0, options.Notes).Select(i => $"Nota {i:D5} {Words[i % Words.Length]}").ToArray();
        var notePaths = titles.Select(t => Join(folders[random.Next(folders.Count)], t + ".md")).ToArray();
        var written = new List<string>(options.Notes + options.Attachments);

        for (var i = 0; i < options.Notes; i++)
        {
            Write(root, notePaths[i], Encoding.UTF8.GetBytes(NoteText(random, titles, i)));
            written.Add(notePaths[i]);
        }

        for (var i = 0; i < options.Attachments; i++)
        {
            var path = Join(folders[random.Next(folders.Count)], $"adjunto-{i:D4}.png");
            var bytes = new byte[random.Next(50 * 1024, 500 * 1024)];
            random.NextBytes(bytes);
            Encoding.ASCII.GetBytes("\u0089PNG\r\n\u001a\n").CopyTo(bytes, 0);
            Write(root, path, bytes);
            written.Add(path);
        }
#pragma warning restore CA5394

        return written;
    }

#pragma warning disable CA5394
    private static List<string> Folders(Random random, VaultGeneratorOptions options)
    {
        var folders = new List<string> { string.Empty };
        var depthOf = new Dictionary<string, int> { [string.Empty] = 0 };
        for (var i = 0; i < options.Folders; i++)
        {
            var candidates = folders.Where(f => depthOf[f] < options.MaxDepth).ToList();
            var parent = candidates[random.Next(candidates.Count)];
            var folder = Join(parent, $"Carpeta {i:D3}");
            folders.Add(folder);
            depthOf[folder] = depthOf[parent] + 1;
        }

        return folders;
    }

    private static string NoteText(Random random, string[] titles, int index)
    {
        var builder = new StringBuilder();
        var tags = Enumerable.Range(0, random.Next(0, 5)).Select(_ => TagNames[random.Next(TagNames.Length)]).Distinct().ToList();
        if (random.Next(3) == 0)
        {
            builder.Append("---\ntitle: ").Append(titles[index]).Append('\n')
                .Append("status: ").Append(random.Next(2) == 0 ? "draft" : "done").Append('\n')
                .Append("rating: ").Append(random.Next(1, 6).ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("tags: [").Append(string.Join(", ", tags.Take(2))).Append("]\n---\n");
        }

        builder.Append("# ").Append(titles[index]).Append("\n\n");

        // ~0.5 % of notes are large (up to 200 KB); the rest average about 2 KB.
        var targetSize = random.Next(200) == 0 ? random.Next(50_000, 200_000) : random.Next(800, 3_200);
        var linksLeft = random.Next(1, 10);
        var tagsLeft = tags.Skip(2).ToList();
        var section = 0;
        while (builder.Length < targetSize)
        {
            if (random.Next(6) == 0)
            {
                builder.Append("\n## Sección ").Append(++section).Append("\n\n");
            }

            var sentence = Enumerable.Range(0, random.Next(8, 20)).Select(_ => Words[random.Next(Words.Length)]).ToList();
            if (linksLeft > 0 && random.Next(2) == 0)
            {
                var target = titles[random.Next(titles.Length)];
                sentence.Insert(random.Next(sentence.Count), random.Next(4) == 0 ? $"[[{target}|alias]]" : $"[[{target}]]");
                linksLeft--;
            }

            if (tagsLeft.Count > 0)
            {
                sentence.Add("#" + tagsLeft[0]);
                tagsLeft.RemoveAt(0);
            }

            builder.Append(string.Join(' ', sentence)).Append('.');
            builder.Append(random.Next(10) == 0 ? $" ^b{random.Next(1000)}\n\n" : "\n\n");
            if (random.Next(8) == 0)
            {
                builder.Append("- [").Append(random.Next(2) == 0 ? ' ' : 'x').Append("] ").Append(Words[random.Next(Words.Length)]).Append('\n');
            }
        }

        return builder.ToString();
    }
#pragma warning restore CA5394

    private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    private static void Write(string root, string relative, byte[] content)
    {
        var full = Path.Combine([root, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }
}
