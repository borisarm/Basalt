using System.Text;
using Basalt.Domain.Common;
using Basalt.Domain.Sync;

namespace Basalt.Domain.Tests.Sync;

public sealed class GitHashingTests
{
    [Theory]
    [InlineData("", "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391")]
    [InlineData("hello\n", "ce013625030ba8dba906f756967f9e9ca394464a")]
    [InlineData("test content\n", "d670460b4b4aece5915caf5c68d12f560a9fe3e4")]
    [InlineData("what is up, doc?", "bd9dbf5aae1a3862dd1526723246b20206e5fc37")]
    [Trait("Spec", "DOM-11")]
    public void Compute_MatchesGitHashObject(string content, string expected)
    {
        GitBlobHasher.Compute(Encoding.UTF8.GetBytes(content)).ToString().Should().Be(expected);
    }

    [Fact]
    public async Task ComputeAsync_Streaming_MatchesInMemory()
    {
        var bytes = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
        using var stream = new MemoryStream(bytes);

        var streamed = await GitBlobHasher.ComputeAsync(stream, bytes.Length, TestContext.Current.CancellationToken);

        streamed.Should().Be(GitBlobHasher.Compute(bytes));
    }

    [Fact]
    public async Task ComputeAsync_LengthMismatch_Throws()
    {
        using var stream = new MemoryStream([1, 2, 3]);

        await FluentActions.Awaiting(() => GitBlobHasher.ComputeAsync(stream, 5, CancellationToken.None)).Should().ThrowAsync<InvalidDataException>();
        await FluentActions.Awaiting(() => GitBlobHasher.ComputeAsync(null!, 0, CancellationToken.None)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391")]
    [InlineData("E69DE29BB2D1D6434B8B29AE775AD8C2E48C5391")]
    public void BlobSha_Parse_NormalizesToLowerCase(string text)
    {
        BlobSha.Parse(text).Value.ToString().Should().Be("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391");
    }

    [Theory]
    [InlineData("")]
    [InlineData("e69de29")]
    [InlineData("e69de29bb2d1d6434b8b29ae775ad8c2e48c539z")]
    [InlineData("e69de29bb2d1d6434b8b29ae775ad8c2e48c53911")]
    public void BlobSha_Parse_RejectsInvalid(string text)
    {
        BlobSha.Parse(text).Error.Code.Should().Be(DomainErrors.ShaInvalid);
        TreeSha.Parse(text).Error.Code.Should().Be(DomainErrors.ShaInvalid);
        CommitSha.Parse(text).Error.Code.Should().Be(DomainErrors.ShaInvalid);
    }

    [Fact]
    public void ShaTypes_AreDistinctAndComparable()
    {
        var blob = BlobSha.Parse("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391").Value;
        var tree = TreeSha.Parse("4b825dc642cb6eb9a060e54bf8d69288fbee4904").Value;
        var commit = CommitSha.Parse("4b825dc642cb6eb9a060e54bf8d69288fbee4904").Value;

        tree.ToString().Should().Be(commit.ToString());
        blob.Should().Be(BlobSha.Parse("E69DE29BB2D1D6434B8B29AE775AD8C2E48C5391").Value);
        blob.ToBytes().Should().HaveCount(20);
        BlobSha.FromBytes(blob.ToBytes()).Should().Be(blob);
        TreeSha.FromBytes(tree.ToBytes()).Should().Be(tree);
        CommitSha.FromBytes(commit.ToBytes()).Should().Be(commit);
        FluentActions.Invoking(() => BlobSha.FromBytes([1, 2])).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => BlobSha.Parse(null!)).Should().Throw<ArgumentNullException>();
        BlobSha.Parse("x").Error.Message.Should().Be("A SHA-1 is 40 hexadecimal characters.");
    }
}
