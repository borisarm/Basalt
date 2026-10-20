using Basalt.Domain.Sync;

namespace Basalt.Domain.Tests.Sync;

public sealed class SyncScopeTests
{
    [Fact]
    [Trait("Spec", "DOM-11")]
    public void Default_IncludesAttachmentsAndLeavesOptInFoldersOut()
    {
        var scope = SyncScope.Default;

        scope.IncludeAttachments.Should().BeTrue();
        scope.IncludeObsidianConfig.Should().BeFalse();
        scope.IncludeTrash.Should().BeFalse();
        scope.ExtraIgnorePatterns.Should().BeNull();
    }

    [Fact]
    [Trait("Spec", "DOM-11")]
    public void With_ChangesOnlyTheSelectedPart()
    {
        var scope = SyncScope.Default with { IncludeTrash = true, ExtraIgnorePatterns = ["draft.md"] };

        scope.IncludeAttachments.Should().BeTrue();
        scope.IncludeTrash.Should().BeTrue();
        scope.ExtraIgnorePatterns.Should().Equal("draft.md");
        scope.Should().NotBe(SyncScope.Default);
    }
}
