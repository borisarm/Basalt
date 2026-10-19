using Basalt.Domain.Common;

namespace Basalt.Domain.Sync;

/// <summary>Shared validation for the 40-character lower-case hexadecimal SHA-1 identifiers of Git objects.</summary>
internal static class Sha
{
    public static Result<string> Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 40 && text.All(char.IsAsciiHexDigit)
            ? text.ToLowerInvariant()
            : new Error(DomainErrors.ShaInvalid, "A SHA-1 is 40 hexadecimal characters.");
    }

    public static string FromBytes(ReadOnlySpan<byte> bytes) =>
        bytes.Length == 20
            ? Convert.ToHexStringLower(bytes)
            : throw new ArgumentException("A SHA-1 has 20 bytes.", nameof(bytes));
}

/// <summary>SHA-1 of a Git blob (spec 02 §11).</summary>
public readonly record struct BlobSha
{
    private BlobSha(string value) => Value = value;

    public string Value { get; }

    public static Result<BlobSha> Parse(string text) => Sha.Normalize(text).Map(v => new BlobSha(v));

    public static BlobSha FromBytes(ReadOnlySpan<byte> bytes) => new(Sha.FromBytes(bytes));

    public byte[] ToBytes() => Convert.FromHexString(Value);

    public override string ToString() => Value;
}

/// <summary>SHA-1 of a Git tree.</summary>
public readonly record struct TreeSha
{
    private TreeSha(string value) => Value = value;

    public string Value { get; }

    public static Result<TreeSha> Parse(string text) => Sha.Normalize(text).Map(v => new TreeSha(v));

    public static TreeSha FromBytes(ReadOnlySpan<byte> bytes) => new(Sha.FromBytes(bytes));

    public byte[] ToBytes() => Convert.FromHexString(Value);

    public override string ToString() => Value;
}

/// <summary>SHA-1 of a Git commit.</summary>
public readonly record struct CommitSha
{
    private CommitSha(string value) => Value = value;

    public string Value { get; }

    public static Result<CommitSha> Parse(string text) => Sha.Normalize(text).Map(v => new CommitSha(v));

    public static CommitSha FromBytes(ReadOnlySpan<byte> bytes) => new(Sha.FromBytes(bytes));

    public byte[] ToBytes() => Convert.FromHexString(Value);

    public override string ToString() => Value;
}
