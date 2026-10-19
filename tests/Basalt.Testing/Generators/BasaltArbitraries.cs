using FsCheck;
using FsCheck.Fluent;
using DomainVaultPath = Basalt.Domain.Notes.VaultPath;

namespace Basalt.Testing.Generators;

/// <summary>FsCheck arbitraries for domain types (spec 09 §3, <c>Arb.VaultPath</c>, ...).</summary>
public static class BasaltArbitraries
{
    private static readonly string[] SegmentCharacters =
    [
        "a", "b", "c", "x", "Z", "N", "0", "7", " ", "-", "_", ".", "#", "é", "ñ", "Ü", "ß", "日", "本", "😀", "é",
    ];

    public static Arbitrary<DomainVaultPath> VaultPath() => VaultPathGen().ToArbitrary();

    public static Gen<string> SegmentGen() =>
        Gen.Choose(1, 8)
            .SelectMany(length => Gen.ArrayOf(Gen.Elements(SegmentCharacters), length))
            .Select(parts => string.Concat(parts))
            .Where(IsValidSegment);

    public static Gen<DomainVaultPath> VaultPathGen() =>
        from count in Gen.Choose(1, 4)
        from segments in Gen.ArrayOf(SegmentGen(), count)
        let candidate = DomainVaultPath.Create(string.Join('/', segments))
        where candidate.IsSuccess
        select candidate.Value;

    private static bool IsValidSegment(string segment)
    {
        var result = DomainVaultPath.Create(segment);
        return result.IsSuccess && result.Value.Segments.Count == 1;
    }
}
