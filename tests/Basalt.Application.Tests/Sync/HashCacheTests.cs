using Basalt.Application.Sync;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Application.Tests.Sync;

public sealed class HashCacheTests
{
    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static BlobSha Sha(byte value) => BlobSha.FromBytes([.. Enumerable.Repeat(value, 20)]);

    private static HashCacheEntry Entry(string path, long size, long ticks, byte sha) => new(P(path), size, ticks, Sha(sha));

    [Fact]
    public void Create_SortsByPathAndExposesTheEntries()
    {
        var cache = HashCache.Create([Entry("b.md", 2, 20, 2), Entry("a.md", 1, 10, 1)]);

        cache.Count.Should().Be(2);
        cache.Entries.Select(entry => entry.Path.ToString()).Should().Equal("a.md", "b.md");
    }

    [Fact]
    public void Create_KeepsTheLastEntryOfADuplicatedPath()
    {
        var cache = HashCache.Create([Entry("a.md", 1, 10, 1), Entry("a.md", 2, 20, 2)]);

        cache.Count.Should().Be(1);
        cache.TryGet(P("a.md"), 2, 20, out var sha).Should().BeTrue();
        sha.Should().Be(Sha(2));
    }

    [Fact]
    public void TryGet_RequiresTheExactSizeAndModificationTime()
    {
        var cache = HashCache.Create([Entry("a.md", 1, 10, 1)]);

        cache.TryGet(P("a.md"), 1, 10, out var sha).Should().BeTrue();
        sha.Should().Be(Sha(1));
        cache.TryGet(P("a.md"), 2, 10, out _).Should().BeFalse();
        cache.TryGet(P("a.md"), 1, 11, out _).Should().BeFalse();
        cache.TryGet(P("b.md"), 1, 10, out _).Should().BeFalse();
    }

    [Fact]
    public void Empty_HasNoEntries()
    {
        var empty = HashCache.Empty;

        empty.Count.Should().Be(0);
        empty.Entries.Should().BeEmpty();
        empty.Should().Be(HashCache.Empty);
        var found = empty.TryGet(P("a.md"), 0, 0, out _);
        found.Should().BeFalse();
        empty.Equals(null).Should().BeFalse();
    }

    [Fact]
    public void Equality_ComparesTheEntries()
    {
        var a = HashCache.Create([Entry("a.md", 1, 10, 1)]);
        var b = HashCache.Create([Entry("a.md", 1, 10, 1)]);
        var c = HashCache.Create([Entry("a.md", 1, 10, 2)]);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        (a != c).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Create_RejectsNullAndTheVaultRoot()
    {
        var createNull = () => HashCache.Create(null!);
        var root = () => HashCache.Create([new HashCacheEntry(VaultPath.Root, 1, 1, Sha(1))]);

        createNull.Should().Throw<ArgumentNullException>();
        root.Should().Throw<ArgumentException>();
    }
}
