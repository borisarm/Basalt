using Basalt.Domain.Common;

namespace Basalt.Domain.Vaults;

/// <summary>Stable vault identity; travels with the vault in <c>.basalt/vault.json</c> (spec 02 §2).</summary>
public readonly record struct VaultId(Guid Value)
{
    public static Result<VaultId> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Guid.TryParse(text, out var guid) && guid != Guid.Empty
            ? new VaultId(guid)
            : new Error(DomainErrors.VaultInvalidId, "Vault ids are non-empty GUIDs.");
    }

    public override string ToString() => Value.ToString("D");
}
