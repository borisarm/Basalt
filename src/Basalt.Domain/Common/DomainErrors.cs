namespace Basalt.Domain.Common;

/// <summary>Catalogue of error codes produced by the domain (spec 13 §3).</summary>
public static class DomainErrors
{
    public const string ResultUninitialized = "result.uninitialized";

    public const string PathOutsideVault = "path.outside_vault";
    public const string PathInvalidSegment = "path.invalid_segment";

    public const string VaultInvalidName = "vault.invalid_name";
    public const string VaultInvalidId = "vault.invalid_id";

    public const string HeadingInvalidLevel = "heading.invalid_level";
    public const string BlockInvalidId = "block.invalid_id";
    public const string TagInvalid = "tag.invalid";
}
