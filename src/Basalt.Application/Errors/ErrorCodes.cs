namespace Basalt.Application.Errors;

/// <summary>
/// Catalogue of application and adapter error codes (spec 13 §3). Domain codes live in
/// <c>Basalt.Domain.Common.DomainErrors</c>. Every code needs a translation <c>Errors.{code}</c> in en and es.
/// </summary>
public static class ErrorCodes
{
    public const string FrontmatterTooLarge = "frontmatter.too_large";
    public const string FrontmatterInvalidYaml = "frontmatter.invalid_yaml";
    public const string FrontmatterNotAMapping = "frontmatter.not_a_mapping";
}
