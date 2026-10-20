using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Domain.Tests.Sync;

public sealed class IgnoreRulesTests
{
    // Patterns and expectations generated with git check-ignore --no-index against a real repository.
    private const string Patterns = """
        # comment
        *.log
        !important.log
        build/
        /root.txt
        doc/*.txt
        doc/**/deep.md
        **/cache/
        a/**/b.txt
        temp?
        [abc].md
        dir/
        !dir/keep.md
        nested/*.tmp
        /anchored/only.md
        *.swp
        """;

    private static readonly IgnoreRules Rules = IgnoreRules.Parse(Patterns);

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    public static TheoryData<string, bool, bool> GitCases => new()
    {
        { "a.log", false, true },
        { "dir/x.log", false, true },
        { "important.log", false, false },
        { "dir/important.log", false, true },
        { "build", true, true },
        { "build", false, false },
        { "build/x.md", false, true },
        { "build/sub/y.md", false, true },
        { "root.txt", false, true },
        { "x/root.txt", false, false },
        { "doc/a.txt", false, true },
        { "doc/sub/a.txt", false, false },
        { "doc/deep.md", false, true },
        { "doc/sub/deep.md", false, true },
        { "cache", true, true },
        { "cache/x.md", false, true },
        { "x/cache", true, true },
        { "x/cache/y.md", false, true },
        { "a/b.txt", false, true },
        { "a/x/b.txt", false, true },
        { "a/x/y/b.txt", false, true },
        { "temp1", false, true },
        { "temp", false, false },
        { "temp12", false, false },
        { "a.md", false, true },
        { "b.md", false, true },
        { "d.md", false, false },
        { "ab.md", false, false },
        { "dir", true, true },
        { "dir/keep.md", false, true },
        { "dir/other.md", false, true },
        { "nested/a.tmp", false, true },
        { "nested/sub/a.tmp", false, false },
        { "anchored/only.md", false, true },
        { "x/anchored/only.md", false, false },
        { "x/y.swp", false, true },
    };

    [Theory]
    [MemberData(nameof(GitCases))]
    [Trait("Spec", "SYNC-3")]
    public void IsIgnored_MatchesGitCheckIgnore(string path, bool isDirectory, bool expected)
    {
        Rules.IsIgnored(P(path), isDirectory).Should().Be(expected);
    }

    public static TheoryData<string, bool, bool> DefaultCases => new()
    {
        { ".git/config", false, true },
        { ".basalt/workspace.pc.json", false, true },
        { ".basalt/workspace.backup.json", false, true },
        { ".basalt/cache", true, true },
        { ".basalt/cache/index.db", false, true },
        { ".~basalt-123.tmp", false, true },
        { "dir/.~basalt-123.tmp", false, true },
        { ".DS_Store", false, true },
        { "sub/.DS_Store", false, true },
        { "Thumbs.db", false, true },
        { "sub/desktop.ini", false, true },
        { "x.swp", false, true },
        { ".basalt/vault.json", false, false },
        { ".basalt/syncignore", false, false },
        { ".basalt/bookmarks.json", false, false },
        { "notes.md", false, false },
    };

    [Theory]
    [MemberData(nameof(DefaultCases))]
    [Trait("Spec", "SYNC-3")]
    public void Defaults_MatchTheAlwaysIgnoredFiles(string path, bool isDirectory, bool expected)
    {
        IgnoreRules.Default.IsIgnored(P(path), isDirectory).Should().Be(expected);
    }

    [Fact]
    [Trait("Spec", "SYNC-3")]
    public void RootAndEmptyAreNeverIgnored()
    {
        IgnoreRules.Parse("*").IsIgnored(VaultPath.Root, isDirectory: true).Should().BeFalse();
        IgnoreRules.Empty.IsIgnored(P("anything.md"), isDirectory: false).Should().BeFalse();
        IgnoreRules.Parse("").IsIgnored(P("anything.md"), isDirectory: false).Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "SYNC-3")]
    public void LaterRulesWin_IncludingNegations()
    {
        var rules = IgnoreRules.Parse(["*.log", "!important.log"]);

        rules.IsIgnored(P("a.log"), false).Should().BeTrue();
        rules.IsIgnored(P("important.log"), false).Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "SYNC-3")]
    public void DefaultsCanBeOverriddenByLaterSets()
    {
        var rules = IgnoreRules.Default.Then(IgnoreRules.Parse(["!*.swp"]));

        rules.IsIgnored(P("x.swp"), isDirectory: false).Should().BeFalse();
        rules.IsIgnored(P(".git/config"), isDirectory: false).Should().BeTrue();
        IgnoreRules.Combine(IgnoreRules.Default, IgnoreRules.Parse("!*.swp")).IsIgnored(P("y.swp"), false).Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "SYNC-3")]
    public void CommentAndBlankLinesAreIgnored()
    {
        var rules = IgnoreRules.Parse("# *.md\n\n  \n*.tmp\n");

        rules.IsIgnored(P("a.md"), false).Should().BeFalse();
        rules.IsIgnored(P("a.tmp"), false).Should().BeTrue();
    }

    [Fact]
    [Trait("Spec", "SYNC-3")]
    public void Parsing_ValidatesArguments()
    {
        FluentActions.Invoking(() => IgnoreRules.Parse((string)null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => IgnoreRules.Parse((IEnumerable<string>)null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => IgnoreRules.Default.Then(null)).Should().NotThrow();
    }
}
