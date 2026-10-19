namespace Basalt.Domain.Common;

/// <summary>Catalogue of error codes produced by the domain (spec 13 §3).</summary>
public static class DomainErrors
{
    public const string ResultUninitialized = "result.uninitialized";

    public const string PathOutsideVault = "path.outside_vault";
    public const string PathInvalidSegment = "path.invalid_segment";
}
