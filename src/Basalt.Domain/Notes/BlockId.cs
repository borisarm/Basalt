using Basalt.Domain.Common;

namespace Basalt.Domain.Notes;

/// <summary>Identifier of a block (<c>^id</c>), stored without the caret (spec 02 §4, 05 §6).</summary>
public readonly record struct BlockId
{
    private BlockId(string value) => Value = value;

    public string Value { get; }

    /// <returns><c>block.invalid_id</c> unless the id is <c>[A-Za-z0-9-]+</c> (an optional leading '^' is accepted).</returns>
    public static Result<BlockId> Create(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var bare = id.StartsWith('^') ? id[1..] : id;
        return bare.Length > 0 && bare.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? new BlockId(bare)
            : new Error(DomainErrors.BlockInvalidId, "Block ids contain only letters A-Z, digits and '-'.");
    }

    public override string ToString() => Value;
}
