using System.Buffers;
using System.Text;
using Basalt.Domain.Common;

namespace Basalt.Domain.Notes;

/// <summary>
/// Canonical path relative to the vault root (spec 02 §3.1): '/' separators, Unicode NFC, no '.'/'..', no empty
/// segments and every segment valid on Windows, so that a vault synchronised from macOS/iOS can always be
/// materialised on Windows. The root is the empty path.
/// </summary>
public readonly record struct VaultPath : IComparable<VaultPath>
{
    private const int MaxSegmentUtf8Bytes = 255;
    private static readonly SearchValues<char> ForbiddenCharacters = SearchValues.Create("<>:\"\\|?*");

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly string? _value;

    private VaultPath(string value) => _value = value.Length == 0 ? null : value;

    /// <summary>The vault root (empty path).</summary>
    public static VaultPath Root => default;

    private string Value => _value ?? string.Empty;

    public bool IsRoot => _value is null;

    public IReadOnlyList<string> Segments => IsRoot ? [] : Value.Split('/');

    /// <summary>Last segment (file or folder name); empty for the root.</summary>
    public string Name => IsRoot ? string.Empty : Value[(Value.LastIndexOf('/') + 1)..];

    /// <summary>Extension in lower case without the dot; empty when there is none (".gitignore" has none).</summary>
    public string Extension
    {
        get
        {
            var name = Name;
            var dot = name.LastIndexOf('.');
            return dot > 0 ? name[(dot + 1)..].ToLowerInvariant() : string.Empty;
        }
    }

    public string NameWithoutExtension
    {
        get
        {
            var name = Name;
            var dot = name.LastIndexOf('.');
            return dot > 0 ? name[..dot] : name;
        }
    }

    public VaultPath Parent
    {
        get
        {
            var slash = Value.LastIndexOf('/');
            return slash < 0 ? Root : new VaultPath(Value[..slash]);
        }
    }

    /// <summary>True when any segment starts with '.'.</summary>
    public bool IsHidden => Segments.Any(s => s.StartsWith('.'));

    /// <summary>Key for case-insensitive comparison (collisions on Windows/macOS file systems).</summary>
    public string CaseFoldKey => Value.ToUpperInvariant();

    /// <summary>Parses and canonicalises a path.</summary>
    /// <returns><c>path.outside_vault</c> when '..' escapes the root; <c>path.invalid_segment</c> (metadata
    /// <c>segment</c>) when a segment is not portable.</returns>
    public static Result<VaultPath> Create(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = new List<string>();
        foreach (var segment in path.Normalize(NormalizationForm.FormC).Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return new Error(DomainErrors.PathOutsideVault, "The path escapes the vault root.");
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            if (ValidateSegment(segment) is { } error)
            {
                return error;
            }

            segments.Add(segment);
        }

        return new VaultPath(string.Join('/', segments));
    }

    /// <summary>Wraps an already canonical path without validation.</summary>
    internal static VaultPath FromTrusted(string canonical) => new(canonical);

    /// <summary>Resolves a relative path (may contain '..') against this folder.</summary>
    public Result<VaultPath> Combine(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        return Create(IsRoot ? relative : $"{Value}/{relative}");
    }

    /// <summary>Replaces the last segment.</summary>
    public Result<VaultPath> WithName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var normalized = name.Normalize(NormalizationForm.FormC);
        if (IsRoot || normalized.Length == 0 || normalized.Contains('/', StringComparison.Ordinal))
        {
            return InvalidSegment(normalized);
        }

        if (ValidateSegment(normalized) is { } error)
        {
            return error;
        }

        return Parent.IsRoot ? new VaultPath(normalized) : new VaultPath($"{Parent.Value}/{normalized}");
    }

    /// <summary>Replaces, adds or (with an empty extension) removes the extension.</summary>
    public Result<VaultPath> ChangeExtension(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        var bare = extension.TrimStart('.');
        return WithName(bare.Length == 0 ? NameWithoutExtension : $"{NameWithoutExtension}.{bare}");
    }

    /// <summary>True when this path is strictly inside <paramref name="ancestor"/>.</summary>
    public bool IsDescendantOf(VaultPath ancestor)
    {
        if (IsRoot)
        {
            return false;
        }

        return ancestor.IsRoot || Value.StartsWith(ancestor.Value + "/", StringComparison.Ordinal);
    }

    /// <summary>Relative path from the folder <paramref name="baseFolder"/> to this path ("../x/y.md").</summary>
    public string RelativeTo(VaultPath baseFolder)
    {
        var target = Segments;
        var from = baseFolder.Segments;
        var common = 0;
        while (common < target.Count && common < from.Count && target[common] == from[common])
        {
            common++;
        }

        var parts = Enumerable.Repeat("..", from.Count - common).Concat(target.Skip(common));
        return string.Join('/', parts);
    }

    public int CompareTo(VaultPath other) => string.CompareOrdinal(Value, other.Value);

    public static bool operator <(VaultPath left, VaultPath right) => left.CompareTo(right) < 0;

    public static bool operator <=(VaultPath left, VaultPath right) => left.CompareTo(right) <= 0;

    public static bool operator >(VaultPath left, VaultPath right) => left.CompareTo(right) > 0;

    public static bool operator >=(VaultPath left, VaultPath right) => left.CompareTo(right) >= 0;

    public override string ToString() => Value;

    private static Error? ValidateSegment(string segment)
    {
        if (Encoding.UTF8.GetByteCount(segment) > MaxSegmentUtf8Bytes
            || segment.AsSpan().ContainsAny(ForbiddenCharacters)
            || segment.Any(c => c < ' ')
            || segment.EndsWith(' ')
            || segment.EndsWith('.')
            || IsReservedName(segment))
        {
            return InvalidSegment(segment);
        }

        return null;
    }

    // Windows reserves device names regardless of the extension ("con.md") and ignores trailing spaces before it.
    private static bool IsReservedName(string segment)
    {
        var dot = segment.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        return ReservedNames.Contains(stem);
    }

    private static Error InvalidSegment(string segment) =>
        new Error(DomainErrors.PathInvalidSegment, "The path contains a segment that is not valid on every platform.")
            .With("segment", segment);
}
