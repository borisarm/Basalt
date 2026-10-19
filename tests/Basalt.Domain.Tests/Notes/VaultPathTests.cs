using Basalt.Domain.Common;
using Basalt.Domain.Notes;

namespace Basalt.Domain.Tests.Notes;

public sealed class VaultPathTests
{
    public static TheoryData<string, string> ValidPaths => new()
    {
        { "", "" },
        { ".", "" },
        { "/", "" },
        { "a", "a" },
        { "a.md", "a.md" },
        { "Notes/Idea.md", "Notes/Idea.md" },
        { "/Notes/Idea.md", "Notes/Idea.md" },
        { "Notes/Idea.md/", "Notes/Idea.md" },
        { "Notes//Idea.md", "Notes/Idea.md" },
        { "Notes\\Idea.md", "Notes/Idea.md" },
        { "Notes\\\\Sub\\Idea.md", "Notes/Sub/Idea.md" },
        { "./a/./b.md", "a/b.md" },
        { "a/b/../c.md", "a/c.md" },
        { "a/b/c/../../d.md", "a/d.md" },
        { "a/..", "" },
        { "a/../b/..", "" },
        { "Proyectos/2026 Q4/Reunión.md", "Proyectos/2026 Q4/Reunión.md" },
        { "Carpeta con espacios/Nota con espacios.md", "Carpeta con espacios/Nota con espacios.md" },
        { ".basalt/vault.json", ".basalt/vault.json" },
        { ".trash/a.md", ".trash/a.md" },
        { ".obsidian/app.json", ".obsidian/app.json" },
        { "a/.hidden", "a/.hidden" },
        { "日本語/ノート.md", "日本語/ノート.md" },
        { "emoji 😀.md", "emoji 😀.md" },
        { "a#b.md", "a#b.md" },
        { "a^b.md", "a^b.md" },
        { "a[b].md", "a[b].md" },
        { "a'b.md", "a'b.md" },
        { "a,b;c.md", "a,b;c.md" },
        { "a&b.md", "a&b.md" },
        { "con-text.md", "con-text.md" },
        { "CONSOLE.md", "CONSOLE.md" },
        { "COM10.md", "COM10.md" },
        { "LPT0.md", "LPT0.md" },
        { "nul_file.md", "nul_file.md" },
        { "auxiliar.md", "auxiliar.md" },
        { "a.b.c.md", "a.b.c.md" },
        { "noextension", "noextension" },
        { "UPPER/lower/MiXeD.MD", "UPPER/lower/MiXeD.MD" },
        { "a/b/c/d/e/f/g/h.md", "a/b/c/d/e/f/g/h.md" },
        { " leading-space.md", " leading-space.md" },
        { "%20encoded.md", "%20encoded.md" },
    };

    public static TheoryData<string, string> InvalidSegments => new()
    {
        { "a<b.md", "a<b.md" },
        { "a>b.md", "a>b.md" },
        { "a:b.md", "a:b.md" },
        { "C:/Users/x.md", "C:" },
        { "a\"b.md", "a\"b.md" },
        { "a|b.md", "a|b.md" },
        { "a?b.md", "a?b.md" },
        { "a*b.md", "a*b.md" },
        { "tab\tname.md", "tab\tname.md" },
        { "nul\0char.md", "nul\0char.md" },
        { "bell\u0007.md", "bell\u0007.md" },
        { "ends-with-space ", "ends-with-space " },
        { "folder /a.md", "folder " },
        { "ends-with-dot.", "ends-with-dot." },
        { "folder./a.md", "folder." },
        { "CON", "CON" },
        { "con.md", "con.md" },
        { "Con.txt", "Con.txt" },
        { "PRN.md", "PRN.md" },
        { "aux.md", "aux.md" },
        { "NUL", "NUL" },
        { "nul.tar.gz", "nul.tar.gz" },
        { "COM1.md", "COM1.md" },
        { "com9", "com9" },
        { "LPT1.md", "LPT1.md" },
        { "lpt9.txt", "lpt9.txt" },
        { "dir/COM3/file.md", "COM3" },
    };

    [Theory]
    [MemberData(nameof(ValidPaths))]
    [Trait("Spec", "DOM-3.1")]
    public void Create_ValidInput_ReturnsCanonicalPath(string input, string expected)
    {
        var result = VaultPath.Create(input);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : null);
        result.Value.ToString().Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(InvalidSegments))]
    [Trait("Spec", "DOM-3.1")]
    public void Create_InvalidSegment_ReturnsInvalidSegmentError(string input, string segment)
    {
        var result = VaultPath.Create(input);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(DomainErrors.PathInvalidSegment);
        result.Error.Metadata.Should().ContainKey("segment").WhoseValue.Should().Be(segment);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../a.md")]
    [InlineData("a/../../b.md")]
    [InlineData("./../x")]
    [Trait("Spec", "DOM-3.1")]
    public void Create_EscapingRoot_ReturnsOutsideVault(string input)
    {
        var result = VaultPath.Create(input);

        result.Error.Code.Should().Be(DomainErrors.PathOutsideVault);
    }

    [Fact]
    public void Create_Null_Throws()
    {
        var act = () => VaultPath.Create(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    [Trait("Spec", "DOM-3.1")]
    public void Create_SegmentOf255Utf8Bytes_IsValid_256IsNot()
    {
        var ok = new string('a', 252) + ".md";
        var tooLong = new string('a', 253) + ".md";
        var multiByte = new string('é', 128);

        VaultPath.Create(ok).IsSuccess.Should().BeTrue();
        VaultPath.Create(tooLong).Error.Code.Should().Be(DomainErrors.PathInvalidSegment);
        VaultPath.Create(multiByte).Error.Code.Should().Be(DomainErrors.PathInvalidSegment);
    }

    [Fact]
    [Trait("Spec", "DOM-3.1")]
    public void Create_NfdInput_IsNormalizedToNfc()
    {
        var nfd = "Reunio\u0301n.md";
        var nfc = "Reuni\u00f3n.md";

        var path = VaultPath.Create(nfd).Value;

        path.ToString().Should().Be(nfc);
        path.Should().Be(VaultPath.Create(nfc).Value);
    }

    [Fact]
    public void Root_IsEmptyAndIsRoot()
    {
        VaultPath.Root.IsRoot.Should().BeTrue();
        VaultPath.Root.ToString().Should().BeEmpty();
        VaultPath.Root.Segments.Should().BeEmpty();
        VaultPath.Root.Name.Should().BeEmpty();
        VaultPath.Root.Parent.Should().Be(VaultPath.Root);
        default(VaultPath).Should().Be(VaultPath.Root);
        default(VaultPath).ToString().Should().BeEmpty();
    }

    [Theory]
    [InlineData("Notes/Idea.md", "Idea.md", "md", "Idea", "Notes")]
    [InlineData("Idea.MD", "Idea.MD", "md", "Idea", "")]
    [InlineData("a/b/archive.tar.gz", "archive.tar.gz", "gz", "archive.tar", "a/b")]
    [InlineData("folder/noext", "noext", "", "noext", "folder")]
    [InlineData(".gitignore", ".gitignore", "", ".gitignore", "")]
    [InlineData(".basalt/vault.json", "vault.json", "json", "vault", ".basalt")]
    public void Properties_DescribeThePath(string input, string name, string extension, string stem, string parent)
    {
        var path = VaultPath.Create(input).Value;

        path.Name.Should().Be(name);
        path.Extension.Should().Be(extension);
        path.NameWithoutExtension.Should().Be(stem);
        path.Parent.ToString().Should().Be(parent);
        path.IsRoot.Should().BeFalse();
    }

    [Fact]
    public void Segments_AreSplitBySlash()
    {
        VaultPath.Create("a/b/c.md").Value.Segments.Should().Equal("a", "b", "c.md");
    }

    [Theory]
    [InlineData("a.md", false)]
    [InlineData(".basalt/vault.json", true)]
    [InlineData("a/.hidden/b.md", true)]
    [InlineData("a/b/.c", true)]
    [InlineData("a.b/c.md", false)]
    public void IsHidden_WhenAnySegmentStartsWithDot(string input, bool hidden)
    {
        VaultPath.Create(input).Value.IsHidden.Should().Be(hidden);
    }

    [Fact]
    public void Combine_AppendsAndNormalizes()
    {
        var folder = VaultPath.Create("Notes").Value;

        folder.Combine("Sub/Idea.md").Value.ToString().Should().Be("Notes/Sub/Idea.md");
        folder.Combine("../Other.md").Value.ToString().Should().Be("Other.md");
        VaultPath.Root.Combine("a.md").Value.ToString().Should().Be("a.md");
        folder.Combine("../../x.md").Error.Code.Should().Be(DomainErrors.PathOutsideVault);
        folder.Combine("bad|name").Error.Code.Should().Be(DomainErrors.PathInvalidSegment);
    }

    [Fact]
    public void WithName_ReplacesLastSegment()
    {
        var path = VaultPath.Create("Notes/A.md").Value;

        path.WithName("B.md").Value.ToString().Should().Be("Notes/B.md");
        path.WithName("a/b").Error.Code.Should().Be(DomainErrors.PathInvalidSegment);
        path.WithName("").Error.Code.Should().Be(DomainErrors.PathInvalidSegment);
        path.WithName("CON.md").Error.Code.Should().Be(DomainErrors.PathInvalidSegment);
        VaultPath.Root.WithName("x").Error.Code.Should().Be(DomainErrors.PathInvalidSegment);
    }

    [Fact]
    public void ChangeExtension_ReplacesAddsOrRemoves()
    {
        VaultPath.Create("a/b.txt").Value.ChangeExtension("md").Value.ToString().Should().Be("a/b.md");
        VaultPath.Create("a/b").Value.ChangeExtension(".md").Value.ToString().Should().Be("a/b.md");
        VaultPath.Create("a/b.md").Value.ChangeExtension("").Value.ToString().Should().Be("a/b");
        VaultPath.Create(".gitignore").Value.ChangeExtension("md").Value.ToString().Should().Be(".gitignore.md");
        VaultPath.Root.ChangeExtension("md").Error.Code.Should().Be(DomainErrors.PathInvalidSegment);
    }

    [Theory]
    [InlineData("a/b.md", "a", true)]
    [InlineData("a/b/c.md", "a", true)]
    [InlineData("a/b.md", "", true)]
    [InlineData("a", "a", false)]
    [InlineData("ab/c.md", "a", false)]
    [InlineData("a", "a/b", false)]
    [InlineData("", "", false)]
    public void IsDescendantOf_IsStrictAndSegmentAware(string path, string ancestor, bool expected)
    {
        VaultPath.Create(path).Value.IsDescendantOf(VaultPath.Create(ancestor).Value).Should().Be(expected);
    }

    [Theory]
    [InlineData("a/b/c.md", "a", "b/c.md")]
    [InlineData("a/b/c.md", "a/b", "c.md")]
    [InlineData("a/c.md", "a/b", "../c.md")]
    [InlineData("x/y.md", "a/b", "../../x/y.md")]
    [InlineData("y.md", "", "y.md")]
    [InlineData("a", "a", "")]
    [InlineData("", "a/b", "../..")]
    public void RelativeTo_ProducesRelativeString(string path, string baseFolder, string expected)
    {
        VaultPath.Create(path).Value.RelativeTo(VaultPath.Create(baseFolder).Value).Should().Be(expected);
    }

    [Fact]
    [Trait("Spec", "DOM-I1")]
    public void CaseFoldKey_IgnoresCaseAndNormalization()
    {
        var a = VaultPath.Create("Notas/Reunio\u0301n.md").Value;
        var b = VaultPath.Create("NOTAS/REUNIÓN.MD").Value;

        a.Should().NotBe(b);
        a.CaseFoldKey.Should().Be(b.CaseFoldKey);
    }

    [Fact]
    public void Equality_IsOrdinalAndComparisonIsOrdinal()
    {
        var a = VaultPath.Create("a.md").Value;
        var upper = VaultPath.Create("A.md").Value;

        a.Should().NotBe(upper);
        a.Should().Be(VaultPath.Create("./a.md").Value);
        a.CompareTo(upper).Should().BePositive();
        upper.CompareTo(a).Should().BeNegative();
        a.CompareTo(a).Should().Be(0);
    }

    [Fact]
    public void Operators_CompareOrdinally()
    {
        var a = VaultPath.Create("a").Value;
        var b = VaultPath.Create("b").Value;

        (a < b).Should().BeTrue();
        (a <= b).Should().BeTrue();
        (b > a).Should().BeTrue();
        (b >= a).Should().BeTrue();
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("COM2")]
    [InlineData("COM3")]
    [InlineData("COM4")]
    [InlineData("COM5")]
    [InlineData("COM6")]
    [InlineData("COM7")]
    [InlineData("COM8")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("LPT2")]
    [InlineData("LPT3")]
    [InlineData("LPT4")]
    [InlineData("LPT5")]
    [InlineData("LPT6")]
    [InlineData("LPT7")]
    [InlineData("LPT8")]
    [InlineData("LPT9")]
    [Trait("Spec", "DOM-3.1")]
    public void Create_EveryReservedDeviceName_IsInvalid(string name)
    {
        VaultPath.Create(name.ToLowerInvariant() + ".md").Error.Code.Should().Be(DomainErrors.PathInvalidSegment);
        VaultPath.Create(name + " .md").Error.Code.Should().Be(DomainErrors.PathInvalidSegment);
    }

    [Fact]
    public void Operators_HandleEqualValues()
    {
        var a = VaultPath.Create("a").Value;
        var same = VaultPath.Create("a").Value;

        (a < same).Should().BeFalse();
        (a <= same).Should().BeTrue();
        (a > same).Should().BeFalse();
        (a >= same).Should().BeTrue();
    }

    [Fact]
    public void Operations_RejectNullArguments()
    {
        var path = VaultPath.Create("a.md").Value;

        FluentActions.Invoking(() => path.Combine(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => path.WithName(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => path.ChangeExtension(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Errors_HaveDescriptiveMessages()
    {
        VaultPath.Create("..").Error.Message.Should().Be("The path escapes the vault root.");
        VaultPath.Create("a|b").Error.Message.Should().Be("The path contains a segment that is not valid on every platform.");
    }

    [Fact]
    public void FromTrusted_SkipsValidation()
    {
        VaultPath.FromTrusted("x/y.md").ToString().Should().Be("x/y.md");
    }
}
