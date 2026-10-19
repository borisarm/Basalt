namespace Basalt.Domain.Common;

/// <summary>
/// An expected failure. <see cref="Code"/> is a stable identifier (<c>module.snake_case</c>) that the UI translates;
/// <see cref="Message"/> is English and meant for logs only.
/// </summary>
public sealed record Error(string Code, string Message, IReadOnlyDictionary<string, object?>? Metadata = null)
{
    /// <summary>Returns a copy of this error with an additional metadata entry.</summary>
    public Error With(string key, object? value)
    {
        var metadata = Metadata is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(Metadata, StringComparer.Ordinal);
        metadata[key] = value;
        return this with { Metadata = metadata };
    }

    public override string ToString() => $"{Code}: {Message}";
}
