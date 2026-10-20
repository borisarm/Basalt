using Avalonia.Media.Imaging;
using Basalt.Domain.Notes;

namespace Basalt.UI.Reading;

/// <summary>
/// Decoded images for reading mode (spec 07 §6): least-recently-used eviction once the decoded size passes
/// <see cref="MaxBytes"/>.
/// </summary>
public sealed class ImageCache : IDisposable
{
    public const long MaxBytes = 100L * 1024 * 1024;

    private readonly Dictionary<VaultPath, LinkedListNode<(VaultPath Path, Bitmap Bitmap, long Size)>> _entries = [];
    private readonly LinkedList<(VaultPath Path, Bitmap Bitmap, long Size)> _order = new();
    private long _size;

    public static ImageCache Shared { get; } = new();

    public long Size => _size;

    public int Count => _entries.Count;

    /// <summary>The bitmap of <paramref name="path"/>, decoded from <paramref name="load"/> on a miss; null if it cannot be decoded.</summary>
    public async Task<Bitmap?> GetAsync(VaultPath path, Func<Task<byte[]?>> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        if (_entries.TryGetValue(path, out var hit))
        {
            _order.Remove(hit);
            _order.AddFirst(hit);
            return hit.Value.Bitmap;
        }

        if (await load().ConfigureAwait(true) is not { Length: > 0 } bytes)
        {
            return null;
        }

        Bitmap bitmap;
        try
        {
            using var stream = new MemoryStream(bytes);
            bitmap = new Bitmap(stream);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            return null;
        }

        var size = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
        _entries[path] = _order.AddFirst((path, bitmap, size));
        _size += size;
        while (_size > MaxBytes && _order.Count > 1)
        {
            var oldest = _order.Last!;
            _order.RemoveLast();
            _entries.Remove(oldest.Value.Path);
            _size -= oldest.Value.Size;
            oldest.Value.Bitmap.Dispose();
        }

        return bitmap;
    }

    public void Dispose()
    {
        foreach (var (_, bitmap, _) in _order)
        {
            bitmap.Dispose();
        }

        _order.Clear();
        _entries.Clear();
        _size = 0;
    }
}
