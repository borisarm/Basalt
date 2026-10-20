using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Basalt.Testing;

namespace Basalt.UI.Tests;

/// <summary>
/// Reference captures of rendered windows (spec 09 §5.5) with the project's own snapshot helper (ADR-0009). Pixels are
/// compared with a small per-channel tolerance so anti-aliasing differences do not fail the build. Baselines are kept
/// per platform because font rasterisation differs between Windows and macOS.
/// </summary>
internal static class PngSnapshot
{
    private const int ChannelTolerance = 8;
    private const double MaxDifferingFraction = 0.005;

    public static void Match(WriteableBitmap frame, string name, [CallerFilePath] string sourceFile = "")
    {
        var directory = Path.Combine(Path.GetDirectoryName(sourceFile)!, "Snapshots");
        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var verified = Path.Combine(directory, $"{name}.{platform}.verified.png");
        var received = Path.Combine(directory, $"{name}.{platform}.received.png");

        using var stream = new MemoryStream();
        frame.Save(stream, new PngBitmapEncoderOptions());
        var actual = stream.ToArray();

        if (Environment.GetEnvironmentVariable("BASALT_ACCEPT_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(verified, actual);
            return;
        }

        if (!File.Exists(verified))
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            Directory.CreateDirectory(directory);
            File.WriteAllBytes(received, actual);
            throw new SnapshotMismatchException($"No verified snapshot at {verified}. Review {received} and rename it to accept.");
        }

        if (Compare(actual, File.ReadAllBytes(verified), out var reason))
        {
            if (File.Exists(received))
            {
                File.Delete(received);
            }

            return;
        }

        Directory.CreateDirectory(directory);
        File.WriteAllBytes(received, actual);
        throw new SnapshotMismatchException($"Snapshot mismatch for {verified}: {reason}. Compare with {received}.");
    }

    private static bool Compare(byte[] actualPng, byte[] expectedPng, out string reason)
    {
        using var actual = Normalize(actualPng);
        using var expected = Normalize(expectedPng);
        if (actual.PixelSize != expected.PixelSize)
        {
            reason = $"size {actual.PixelSize} vs {expected.PixelSize}";
            return false;
        }

        using var actualBuffer = actual.Lock();
        using var expectedBuffer = expected.Lock();
        var width = actualBuffer.Size.Width;
        var height = actualBuffer.Size.Height;
        var actualPixels = ReadPixels(actualBuffer);
        var expectedPixels = ReadPixels(expectedBuffer);
        var differing = 0;
        for (var y = 0; y < height; y++)
        {
            var row = y * actualBuffer.RowBytes;
            for (var x = 0; x < width; x++)
            {
                var i = row + (x * 4);
                if (Math.Abs(actualPixels[i] - expectedPixels[i]) > ChannelTolerance
                    || Math.Abs(actualPixels[i + 1] - expectedPixels[i + 1]) > ChannelTolerance
                    || Math.Abs(actualPixels[i + 2] - expectedPixels[i + 2]) > ChannelTolerance
                    || Math.Abs(actualPixels[i + 3] - expectedPixels[i + 3]) > ChannelTolerance)
                {
                    differing++;
                }
            }
        }

        var fraction = differing / (double)(width * height);
        reason = $"{fraction:P2} of pixels differ (limit {MaxDifferingFraction:P2})";
        return fraction <= MaxDifferingFraction;
    }

    private static WriteableBitmap Normalize(byte[] png)
    {
        using var stream = new MemoryStream(png);
        using var bitmap = new Bitmap(stream);
        var normalized = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = normalized.Lock();
        bitmap.CopyPixels(buffer);
        return normalized;
    }

    private static byte[] ReadPixels(ILockedFramebuffer buffer)
    {
        var pixels = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);
        return pixels;
    }
}
