using System.Text;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Testing.Generators;

namespace Basalt.Domain.Tests.Sync;

public sealed class GitObjectHashingTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 19, 12, 34, 56, TimeSpan.FromHours(2));

    private static BlobSha Blob(string text) => GitBlobHasher.Compute(Encoding.UTF8.GetBytes(text));

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static GitSignature Signature => new("Basalt", "basalt@example.com", When);

    private static TreeSha Tree2()
    {
        var foo = GitTreeHasher.Compute([GitTreeEntry.File("bar.txt", Blob("x\n"))]);
        foo.ToString().Should().Be("6577ecdb6cc1b200df813d6cf8f9ffa8465bc7ad");
        return GitTreeHasher.Compute(
        [
            GitTreeEntry.File("a.txt", Blob("hello\n")),
            GitTreeEntry.File("foo.bar", Blob("y\n")),
            GitTreeEntry.Directory("foo", foo),
        ]);
    }

    [Fact]
    [Trait("Spec", "SYNC-9.4")]
    public void Tree_Empty_IsTheKnownEmptyTree()
    {
        GitTreeHasher.Compute([]).ToString().Should().Be("4b825dc642cb6eb9a060e54bf8d69288fbee4904");
    }

    [Fact]
    [Trait("Spec", "SYNC-9.4")]
    public void Tree_FromManifest_MatchesGitWriteTree()
    {
        var files = new Dictionary<VaultPath, BlobSha>
        {
            [P("a.txt")] = Blob("hello\n"),
            [P("dir/b.txt")] = Blob("test content\n"),
        };

        GitTreeHasher.Compute(files).ToString().Should().Be("eb283040702caa2bf24ffba56e68a5011fc9847e");
    }

    [Fact]
    [Trait("Spec", "SYNC-9.4")]
    public void Tree_SortsDirectoriesAsIfTheyEndedWithSlash()
    {
        Tree2().ToString().Should().Be("12899b92c74c0c98363a7290dc64c358954744a6");
    }

    [Fact]
    [Trait("Spec", "SYNC-9.4")]
    public void Tree_DoesNotDependOnInsertionOrder()
    {
        var foo = GitTreeHasher.Compute([GitTreeEntry.File("bar.txt", Blob("x\n"))]);
        GitTreeEntry[] entries =
        [
            GitTreeEntry.File("a.txt", Blob("hello\n")),
            GitTreeEntry.File("foo.bar", Blob("y\n")),
            GitTreeEntry.Directory("foo", foo),
        ];
        var expected = GitTreeHasher.Compute(entries);

        expected.Should().Be(Tree2());
        GitTreeHasher.Compute([entries[2], entries[0], entries[1]]).Should().Be(expected);
        GitTreeHasher.Compute([entries[1], entries[2], entries[0]]).Should().Be(expected);
    }

    [BasaltProperty]
    [Trait("Spec", "SYNC-9.4")]
    public bool Tree_IsOrderInvariant_ForAnyPermutation(int[] keys)
    {
        var foo = GitTreeHasher.Compute([GitTreeEntry.File("bar.txt", Blob("x\n"))]);
        GitTreeEntry[] entries =
        [
            GitTreeEntry.File("a.txt", Blob("hello\n")),
            GitTreeEntry.File("foo.bar", Blob("y\n")),
            GitTreeEntry.Directory("foo", foo),
        ];
        var expected = Tree2();
        var shuffled = entries
            .Select((entry, i) => (Entry: entry, Key: i < keys.Length ? keys[i] : 0))
            .OrderBy(x => x.Key)
            .ThenBy(x => x.Entry.Name, StringComparer.Ordinal)
            .Select(x => x.Entry)
            .ToArray();

        return GitTreeHasher.Compute(shuffled) == expected;
    }

    [Fact]
    [Trait("Spec", "SYNC-9.4")]
    public void Tree_FromManifest_HandlesNestedFoldersAndUnicode()
    {
        var files = new Dictionary<VaultPath, BlobSha>
        {
            [P("café/nota.md")] = Blob("# café\n"),
            [P("café/子.md")] = Blob("子\n"),
            [P("raíz.md")] = Blob("raíz\n"),
        };

        var tree = GitTreeHasher.Compute(files);

        tree.ToString().Should().MatchRegex("^[0-9a-f]{40}$");
        GitTreeHasher.Compute(files.Reverse().ToDictionary()).Should().Be(tree);
    }

    [Fact]
    [Trait("Spec", "SYNC-9.4")]
    public void TreeEntry_ValidatesNames()
    {
        var blob = Blob("x");
        FluentActions.Invoking(() => GitTreeEntry.File("", blob)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => GitTreeEntry.File("a/b", blob)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => GitTreeEntry.File("a\0b", blob)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => GitTreeEntry.File("..", blob)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => GitTreeEntry.Directory(".", Tree2())).Should().Throw<ArgumentException>();
    }

    [Fact]
    [Trait("Spec", "SYNC-9.4")]
    public void Tree_RejectsFileAndFolderWithTheSameName()
    {
        var files = new Dictionary<VaultPath, BlobSha>
        {
            [P("foo")] = Blob("file"),
            [P("foo/bar.txt")] = Blob("nested"),
        };

        FluentActions.Invoking(() => GitTreeHasher.Compute(files)).Should().Throw<ArgumentException>();
    }

    [Fact]
    [Trait("Spec", "SYNC-9.4")]
    public void Commit_MatchesGitCommitTree()
    {
        var commit = GitCommitHasher.Compute(Tree2(), Signature, Signature, "Initial sync");

        commit.ToString().Should().Be("8b5c2337fa39e475d69e8160144d8c05fb22cf4f");
    }

    [Fact]
    [Trait("Spec", "SYNC-9.4")]
    public void Commit_WithParent_MatchesGitCommitTree()
    {
        var parent = CommitSha.Parse("8b5c2337fa39e475d69e8160144d8c05fb22cf4f").Value;

        var commit = GitCommitHasher.Compute(Tree2(), [parent], Signature, Signature, "Second");

        commit.ToString().Should().Be("403dd03e71bd86c1818493d3c58d6bc33d3222ec");
    }

    [Fact]
    [Trait("Spec", "SYNC-9.4")]
    public void Commit_KeepsAnExistingFinalNewline()
    {
        GitCommitHasher.Compute(Tree2(), Signature, Signature, "Initial sync\n")
            .Should().Be(GitCommitHasher.Compute(Tree2(), Signature, Signature, "Initial sync"));
    }

    [Fact]
    [Trait("Spec", "SYNC-9.4")]
    public void Commit_AllowsEmptyMessageAndMultipleParents()
    {
        var first = CommitSha.Parse("8b5c2337fa39e475d69e8160144d8c05fb22cf4f").Value;
        var second = CommitSha.Parse("403dd03e71bd86c1818493d3c58d6bc33d3222ec").Value;

        var commit = GitCommitHasher.Compute(Tree2(), [first, second], Signature, Signature, string.Empty);

        commit.ToString().Should().MatchRegex("^[0-9a-f]{40}$");
        commit.Should().NotBe(GitCommitHasher.Compute(Tree2(), [first], Signature, Signature, string.Empty));
    }

    [Fact]
    [Trait("Spec", "SYNC-9.4")]
    public void CommitSignature_IsAValueObject()
    {
        var signature = new GitSignature("Basalt", "basalt@example.com", When);
        signature.Should().Be(Signature);
        signature.ToString().Should().Be("Basalt <basalt@example.com> 1792406096 +0200");
    }

    [Fact]
    [Trait("Spec", "SYNC-9.4")]
    public void Commit_ValidatesArguments()
    {
        var tree = Tree2();
        FluentActions.Invoking(() => GitCommitHasher.Compute(tree, Signature, Signature, null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => GitCommitHasher.Compute(tree, new GitSignature("", "a@b", When), Signature, "m"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => GitCommitHasher.Compute(tree, Signature, new GitSignature("Basalt", "", When), "m"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => GitCommitHasher.Compute(tree, new GitSignature("a\nb", "a@b", When), Signature, "m"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => GitCommitHasher.Compute(tree, [default], Signature, Signature, "m"))
            .Should().Throw<ArgumentException>();
    }
}
