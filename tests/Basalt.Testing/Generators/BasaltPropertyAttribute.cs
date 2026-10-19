using FsCheck.Xunit;

namespace Basalt.Testing.Generators;

/// <summary>
/// FsCheck property with Basalt arbitraries registered. Runs 100 cases by default and
/// <c>BASALT_PROPERTY_RUNS</c> cases when that variable is set (nightly: 10 000) — spec 09 §6.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BasaltPropertyAttribute : PropertyAttribute
{
    public BasaltPropertyAttribute()
    {
        Arbitrary = [typeof(BasaltArbitraries)];
        MaxTest = int.TryParse(Environment.GetEnvironmentVariable("BASALT_PROPERTY_RUNS"), out var runs) && runs > 0 ? runs : 100;
    }
}
