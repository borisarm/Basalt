using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Basalt.Domain.Sync;

/// <summary>Git blob hashing: SHA1("blob {length}\0" + bytes) (spec 02 §11). Deterministic, BCL only.</summary>
public static class GitBlobHasher
{
    private const int BufferSize = 81920;

    public static BlobSha Compute(ReadOnlySpan<byte> content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Header(content.Length));
        hash.AppendData(content);
        return BlobSha.FromBytes(hash.GetHashAndReset());
    }

    /// <summary>Streams <paramref name="length"/> bytes; throws <see cref="InvalidDataException"/> when the stream
    /// does not contain exactly that many bytes (e.g. the file changed while hashing).</summary>
    public static async Task<BlobSha> ComputeAsync(Stream content, long length, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Header(length));
        var buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            total += read;
        }

        if (total != length)
        {
            throw new InvalidDataException($"Expected {length} bytes but read {total}.");
        }

        return BlobSha.FromBytes(hash.GetHashAndReset());
    }

    private static byte[] Header(long length) => Encoding.ASCII.GetBytes("blob " + length.ToString(CultureInfo.InvariantCulture) + "\0");
}
