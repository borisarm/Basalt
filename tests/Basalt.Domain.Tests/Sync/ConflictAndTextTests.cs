using System.Text;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Domain.Tests.Sync;

public sealed class ConflictCopyNamerTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 19, 15, 42, 33, TimeSpan.FromHours(2));

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    [Fact]
    [Trait("Spec", "SYNC-5")]
    public void Name_UsesTheConflictFormat_AndKeepsTheExtension()
    {
        var name = ConflictCopyNamer.Name(P("Nota.md"), [], When, "MacBook").Value;

        name.ToString().Should().Be("Nota (conflict 2026-10-19 154233 MacBook).md");
    }

    [Fact]
    [Trait("Spec", "SYNC-5")]
    public void Name_KeepsFolderAndHandlesCompoundExtensions()
    {
        var name = ConflictCopyNamer.Name(P("Carpeta/archive.tar.gz"), [], When, "PC").Value;

        name.ToString().Should().Be("Carpeta/archive.tar (conflict 2026-10-19 154233 PC).gz");
    }

    [Fact]
    [Trait("Spec", "SYNC-5")]
    public void Name_HandlesFilesWithoutExtension()
    {
        ConflictCopyNamer.Name(P("README"), [], When, "PC").Value.ToString()
            .Should().Be("README (conflict 2026-10-19 154233 PC)");
        ConflictCopyNamer.Name(P(".gitignore"), [], When, "PC").Value.ToString()
            .Should().Be(".gitignore (conflict 2026-10-19 154233 PC)");
    }

    [Fact]
    [Trait("Spec", "SYNC-5")]
    public void Name_AddsSuffixesUntilThePathIsFree()
    {
        var first = P("Nota (conflict 2026-10-19 154233 MacBook).md");
        var second = P("Nota (conflict 2026-10-19 154233 MacBook 2).md");

        ConflictCopyNamer.Name(P("Nota.md"), [first], When, "MacBook").Value.Should().Be(second);
        ConflictCopyNamer.Name(P("Nota.md"), [first, second], When, "MacBook").Value.ToString()
            .Should().Be("Nota (conflict 2026-10-19 154233 MacBook 3).md");
    }

    [Fact]
    [Trait("Spec", "SYNC-5")]
    public void Name_ComparesExistingPathsCaseInsensitively()
    {
        var taken = P("Nota (conflict 2026-10-19 154233 MacBook).md");

        ConflictCopyNamer.Name(P("Nota.md"), [taken], When, "MacBook").Value.ToString()
            .Should().EndWith("MacBook 2).md");
    }

    [Fact]
    [Trait("Spec", "SYNC-5")]
    public void Name_ReplacesInvalidDeviceCharacters()
    {
        var name = ConflictCopyNamer.Name(P("Nota.md"), [], When, "Mac/Book: \"work\"?").Value;

        name.Name.Should().Be("Nota (conflict 2026-10-19 154233 Mac-Book- -work--).md");
        name.ToString().Should().NotContain("/").And.NotContain(":").And.NotContain("\"");
    }

    [Fact]
    [Trait("Spec", "SYNC-5")]
    public void Name_FallsBackWhenTheDeviceNameIsEmptyAfterSanitising()
    {
        ConflictCopyNamer.Name(P("Nota.md"), [], When, "///").Value.Name
            .Should().Be("Nota (conflict 2026-10-19 154233 device).md");
        ConflictCopyNamer.Name(P("Nota.md"), [], When, "  ").Value.Name
            .Should().Be("Nota (conflict 2026-10-19 154233 device).md");
    }

    [Fact]
    [Trait("Spec", "SYNC-5")]
    public void Name_ValidatesArguments()
    {
        FluentActions.Invoking(() => ConflictCopyNamer.Name(VaultPath.Root, [], When, "PC")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ConflictCopyNamer.Name(P("a.md"), null!, When, "PC")).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => ConflictCopyNamer.Name(P("a.md"), [], When, null!)).Should().Throw<ArgumentNullException>();
    }
}

public sealed class TextBinaryClassifierTests
{
    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    [Theory]
    [InlineData("md")]
    [InlineData("txt")]
    [InlineData("json")]
    [InlineData("yaml")]
    [InlineData("yml")]
    [InlineData("css")]
    [InlineData("js")]
    [InlineData("canvas")]
    [InlineData("csv")]
    [InlineData("svg")]
    [InlineData("html")]
    [InlineData("xml")]
    [Trait("Spec", "SYNC-5")]
    public void IsText_AcceptsTheConfiguredExtensions_WithValidUtf8(string extension)
    {
        TextBinaryClassifier.IsText(P("file." + extension), Encoding.UTF8.GetBytes("hola, ñ")).Should().BeTrue();
        TextBinaryClassifier.IsText(P("FILE." + extension.ToUpperInvariant()), Encoding.UTF8.GetBytes("x")).Should().BeTrue();
    }

    [Theory]
    [InlineData("png")]
    [InlineData("pdf")]
    [InlineData("mdx")]
    [InlineData("")]
    [Trait("Spec", "SYNC-5")]
    public void IsText_RejectsOtherExtensions(string extension)
    {
        TextBinaryClassifier.IsText(P("file" + (extension.Length == 0 ? string.Empty : "." + extension)), "hola"u8).Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "SYNC-5")]
    public void IsText_RejectsInvalidUtf8()
    {
        TextBinaryClassifier.IsText(P("a.md"), [0x68, 0x6F, 0xC3, 0x28]).Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "SYNC-5")]
    public void IsText_RejectsNulWithinTheFirst8K()
    {
        var content = Enumerable.Repeat((byte)'a', 8192).ToArray();
        content[8191] = 0;

        TextBinaryClassifier.IsText(P("a.md"), content).Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "SYNC-5")]
    public void IsText_AllowsNulAfterTheFirst8K()
    {
        var content = Enumerable.Repeat((byte)'a', 8193).ToArray();
        content[8192] = 0;

        TextBinaryClassifier.IsText(P("a.md"), content).Should().BeTrue();
    }

    [Fact]
    [Trait("Spec", "SYNC-5")]
    public void IsText_AcceptsEmptyTextFiles()
    {
        TextBinaryClassifier.IsText(P("a.md"), ReadOnlySpan<byte>.Empty).Should().BeTrue();
    }
}
