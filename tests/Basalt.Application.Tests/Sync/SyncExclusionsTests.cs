using Basalt.Application.Sync;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Application.Tests.Sync;

public sealed class SyncExclusionsTests
{
    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    [Theory]
    [InlineData(".obsidian/app.json")]
    [InlineData(".trash/A.md")]
    public void IsIgnored_ByDefault_ExcludesHiddenFolders(string path) =>
        SyncExclusions.IsIgnored(P(path), SyncScope.Default, IgnoreRules.Empty).Should().BeTrue();

    [Theory]
    [InlineData("A.md")]
    [InlineData(".basalt/vault.json")]
    [InlineData("img.png")]
    public void IsIgnored_ByDefault_KeepsNotesConfigAndAttachments(string path) =>
        SyncExclusions.IsIgnored(P(path), SyncScope.Default, IgnoreRules.Empty).Should().BeFalse();

    [Theory]
    [InlineData("img.png", true)]
    [InlineData("A.md", false)]
    [InlineData(".basalt/vault.json", false)]
    public void IsIgnored_WithoutAttachments_OnlyKeepsMarkdownAndSharedConfig(string path, bool expected) =>
        SyncExclusions.IsIgnored(P(path), SyncScope.Default with { IncludeAttachments = false }, IgnoreRules.Empty)
            .Should().Be(expected);

    [Theory]
    [InlineData(".obsidian/app.json")]
    [InlineData(".trash/A.md")]
    [InlineData("img.png")]
    public void IsIgnored_WithEverythingIncluded_KeepsTheFile(string path)
    {
        var scope = new SyncScope(IncludeAttachments: true, IncludeObsidianConfig: true, IncludeTrash: true);

        SyncExclusions.IsIgnored(P(path), scope, IgnoreRules.Empty).Should().BeFalse();
    }

    [Fact]
    public void IsIgnored_AppliesTheSyncIgnoreRulesFirst()
    {
        var scope = new SyncScope(IncludeAttachments: true, IncludeObsidianConfig: true, IncludeTrash: true);

        SyncExclusions.IsIgnored(P("draft/img.png"), scope, IgnoreRules.Parse("draft/")).Should().BeTrue();
    }

    [Fact]
    public void IsIgnored_RejectsNullScopeAndRules()
    {
        FluentActions.Invoking(() => SyncExclusions.IsIgnored(P("A.md"), null!, IgnoreRules.Empty))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => SyncExclusions.IsIgnored(P("A.md"), SyncScope.Default, null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("Notas/A.md", "Notas", true)]
    [InlineData("Notas/A.md", "Otras", false)]
    public void IsUnder_ComparesTheFirstSegment(string path, string folder, bool expected) =>
        SyncExclusions.IsUnder(P(path), folder).Should().Be(expected);

    [Fact]
    public void IsUnder_Root_IsFalse() => SyncExclusions.IsUnder(VaultPath.Root, "Notas").Should().BeFalse();
}
