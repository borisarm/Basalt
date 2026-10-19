namespace Basalt.Domain.Common;

/// <summary>The "no value" type for <c>Result&lt;Unit&gt;</c>.</summary>
public readonly record struct Unit
{
    public static Unit Value => default;

    public override string ToString() => "()";
}
