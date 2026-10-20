using System.Globalization;
using Basalt.VaultGenerator;

// Usage: dotnet run --project tools/Basalt.VaultGenerator -- <output-folder> [--seed N] [--notes N] [--attachments N] [--folders N]
if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync("Usage: Basalt.VaultGenerator <output-folder> [--seed N] [--notes N] [--attachments N] [--folders N]");
    return 1;
}

var options = new VaultGeneratorOptions();
for (var i = 1; i + 1 < args.Length; i += 2)
{
    var value = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
    options = args[i] switch
    {
        "--seed" => options with { Seed = value },
        "--notes" => options with { Notes = value },
        "--attachments" => options with { Attachments = value },
        "--folders" => options with { Folders = value },
        _ => throw new ArgumentException($"Unknown option {args[i]}"),
    };
}

var written = ReferenceVaultGenerator.Generate(args[0], options);
Console.WriteLine($"Generated {written.Count} files in {args[0]}");
return 0;
