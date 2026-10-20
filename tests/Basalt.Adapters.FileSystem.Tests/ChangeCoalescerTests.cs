using Basalt.Application.Ports.FileSystem;
using Basalt.Domain.Notes;
using static Basalt.Application.Ports.FileSystem.FileSystemChangeKind;

namespace Basalt.Adapters.FileSystem.Tests;

public sealed class ChangeCoalescerTests
{
    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private readonly ChangeCoalescer _coalescer = new();

    [Fact]
    public void CreatedThenChanged_IsCreated()
    {
        _coalescer.Add(Created, "a.md");
        _coalescer.Add(Changed, "a.md");

        _coalescer.Flush().Should().Equal(new FileSystemChange(Created, P("a.md")));
    }

    [Fact]
    public void CreatedThenDeleted_IsDeleted()
    {
        _coalescer.Add(Created, "a.md");
        _coalescer.Add(Deleted, "a.md");

        _coalescer.Flush().Should().Equal(new FileSystemChange(Deleted, P("a.md")));
        _coalescer.HasPending.Should().BeFalse();
    }

    [Fact]
    public void DeletedThenCreated_IsChanged()
    {
        _coalescer.Add(Deleted, "a.md");
        _coalescer.Add(Created, "a.md");

        _coalescer.Flush().Should().Equal(new FileSystemChange(Changed, P("a.md")));
    }

    [Fact]
    public void AtomicWrite_TempRenamedOntoTarget_IsChanged()
    {
        _coalescer.Add(Created, ".~basalt-1.tmp");
        _coalescer.Add(Changed, ".~basalt-1.tmp");
        _coalescer.Add(Renamed, "a.md", ".~basalt-1.tmp");

        _coalescer.Flush().Should().Equal(new FileSystemChange(Changed, P("a.md")));
    }

    [Fact]
    public void RenameIntoTemp_IsDeletion()
    {
        _coalescer.Add(Renamed, ".~basalt-2.tmp", "a.md");

        _coalescer.Flush().Should().Equal(new FileSystemChange(Deleted, P("a.md")));
    }

    [Fact]
    public void Rename_ReplacesPendingChangeOfOldPath()
    {
        _coalescer.Add(Changed, "old.md");
        _coalescer.Add(Renamed, "new.md", "old.md");
        _coalescer.Add(Changed, "new.md");

        _coalescer.Flush().Should().Equal(new FileSystemChange(Renamed, P("new.md"), P("old.md")));
    }

    [Fact]
    public void Overflow_ReplacesEverythingWithRescan()
    {
        _coalescer.Add(Created, "a.md");
        _coalescer.Overflow();

        _coalescer.Flush().Should().Equal(new FileSystemChange(RescanRequired, VaultPath.Root));
        _coalescer.Flush().Should().BeEmpty();
    }

    [Fact]
    public void InvalidOrRootPaths_AreIgnored()
    {
        _coalescer.Add(Created, "a:b.md");
        _coalescer.Add(Changed, ".");
        _coalescer.Add(Renamed, "x.md", "a:b.md");

        _coalescer.Flush().Should().Equal(new FileSystemChange(Changed, P("x.md")));
    }

    [Fact]
    public void Order_FollowsFirstOccurrence()
    {
        _coalescer.Add(Changed, "b.md");
        _coalescer.Add(Changed, "a.md");
        _coalescer.Add(Changed, "b.md");

        _coalescer.Flush().Select(c => c.Path.ToString()).Should().Equal("b.md", "a.md");
    }
}
