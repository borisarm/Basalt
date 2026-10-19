using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Basalt.Testing;

/// <summary>
/// Minimal approval/snapshot testing (ADR-0009). The value is serialised to canonical JSON and compared with
/// <c>{TestFile}.{Member}[.{case}].verified.json</c> next to the test file. On mismatch a <c>.received.json</c>
/// is written and the assertion fails. Set <c>BASALT_ACCEPT_SNAPSHOTS=1</c> to accept received output.
/// </summary>
public static class Snapshot
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Match(
        object? value,
        string? caseName = null,
        [CallerFilePath] string sourceFile = "",
        [CallerMemberName] string member = "")
    {
        var text = value as string ?? JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), Options);
        MatchText(text, "json", caseName, sourceFile, member);
    }

    public static void MatchText(
        string text,
        string extension = "txt",
        string? caseName = null,
        [CallerFilePath] string sourceFile = "",
        [CallerMemberName] string member = "")
    {
        var directory = Path.GetDirectoryName(sourceFile)!;
        var stem = $"{Path.GetFileNameWithoutExtension(sourceFile)}.{member}{(caseName is null ? string.Empty : "." + Sanitize(caseName))}";
        MatchFile(text, Path.Combine(directory, stem), extension);
    }

    /// <summary>Compares JSON for <paramref name="value"/> with <c>{pathWithoutExtension}.verified.json</c>.</summary>
    public static void MatchFile(object? value, string pathWithoutExtension) =>
        MatchFile(value as string ?? JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), Options), pathWithoutExtension, "json");

    /// <summary>Compares text with <c>{pathWithoutExtension}.verified.txt</c>.</summary>
    public static void MatchTextFile(string text, string pathWithoutExtension) => MatchFile(text, pathWithoutExtension, "txt");

    private static void MatchFile(string text, string pathWithoutExtension, string extension)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var verified = $"{pathWithoutExtension}.verified.{extension}";
        var received = $"{pathWithoutExtension}.received.{extension}";

        var expected = File.Exists(verified) ? File.ReadAllText(verified).Replace("\r\n", "\n", StringComparison.Ordinal) : null;
        if (expected == normalized)
        {
            if (File.Exists(received))
            {
                File.Delete(received);
            }

            return;
        }

        if (Environment.GetEnvironmentVariable("BASALT_ACCEPT_SNAPSHOTS") == "1")
        {
            File.WriteAllText(verified, normalized);
            return;
        }

        File.WriteAllText(received, normalized);
        throw new SnapshotMismatchException(expected is null
            ? $"No verified snapshot at {verified}. Review {received} and rename it to accept."
            : $"Snapshot mismatch for {verified}. Compare with {received}.");
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}

public sealed class SnapshotMismatchException(string message) : Exception(message);
