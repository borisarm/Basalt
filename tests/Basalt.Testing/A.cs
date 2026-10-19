namespace Basalt.Testing;

/// <summary>
/// Entry point for fluent test-data builders (<c>A.Note()</c>, <c>A.Manifest()</c>...). Each module adds its builders
/// in a partial declaration next to the builder type.
/// </summary>
public static partial class A
{
    /// <summary>A deterministic Bogus-free seed shared by builders that need pseudo-random data.</summary>
    public const int Seed = 20261019;
}
