namespace Basalt.Testing.FakeGitHub;

/// <summary>A request observed by the fake server (spec 06 §14).</summary>
public sealed record RequestRecord(
    DateTimeOffset Timestamp,
    string Method,
    string Path,
    string Query,
    IReadOnlyDictionary<string, string> Headers)
{
    public string? Authorization => Headers.TryGetValue("Authorization", out var value) ? value : null;
}
