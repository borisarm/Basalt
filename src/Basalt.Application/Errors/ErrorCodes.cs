namespace Basalt.Application.Errors;

/// <summary>
/// Catalogue of application and adapter error codes (spec 13 §3). Domain codes live in
/// <c>Basalt.Domain.Common.DomainErrors</c>. Every code needs a translation <c>Errors.{code}</c> in en and es.
/// </summary>
public static class ErrorCodes
{
    public const string FsNotFound = "fs.not_found";
    public const string FsAlreadyExists = "fs.already_exists";
    public const string FsIsDirectory = "fs.is_directory";
    public const string FsNotADirectory = "fs.not_a_directory";
    public const string FsConcurrentModification = "fs.concurrent_modification";
    public const string FsAccessDenied = "fs.access_denied";
    public const string FsIo = "fs.io";

    public const string StorageIo = "storage.io";

    public const string VaultAlreadyRegistered = "vault.already_registered";
    public const string VaultNotRegistered = "vault.not_registered";

    public const string AccountNotFound = "account.not_found";

    public const string FrontmatterTooLarge = "frontmatter.too_large";
    public const string FrontmatterInvalidYaml = "frontmatter.invalid_yaml";
    public const string FrontmatterNotAMapping = "frontmatter.not_a_mapping";
}
