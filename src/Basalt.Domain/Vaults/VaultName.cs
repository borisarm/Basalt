using System.Buffers;
using Basalt.Domain.Common;

namespace Basalt.Domain.Vaults;

/// <summary>Display name of a vault: 1–100 characters, no surrounding spaces, no path characters (spec 02 §2).</summary>
public sealed record VaultName
{
    private const int MaxLength = 100;
    private static readonly SearchValues<char> Forbidden = SearchValues.Create("/\\:*?\"<>|");

    private VaultName(string value) => Value = value;

    public string Value { get; }

    /// <returns><c>vault.invalid_name</c> when the rules are not met.</returns>
    public static Result<VaultName> Create(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is 0 or > MaxLength || name.Trim().Length != name.Length || name.AsSpan().ContainsAny(Forbidden))
        {
            return new Error(DomainErrors.VaultInvalidName, "Vault names must have 1-100 characters, no surrounding spaces and no / \\ : * ? \" < > |.");
        }

        return new VaultName(name);
    }

    public override string ToString() => Value;
}
