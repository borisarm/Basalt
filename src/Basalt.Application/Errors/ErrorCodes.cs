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
    public const string FsTrashUnavailable = "fs.trash_unavailable";

    public const string StorageIo = "storage.io";

    public const string PathAlreadyExists = "path.already_exists";
    public const string EntryNotFound = "entry.not_found";
    public const string NoteModifiedExternally = "note.modified_externally";
    public const string NoteNotATask = "note.not_a_task";
    public const string NoteFrontmatterInvalid = "note.frontmatter_invalid";
    public const string MoveIntoItself = "entry.move_into_itself";
    public const string MentionNotFound = "mention.not_found";

    public const string VaultAlreadyRegistered = "vault.already_registered";
    public const string VaultNotRegistered = "vault.not_registered";
    public const string VaultNotOpen = "vault.not_open";
    public const string VaultAlreadyOpen = "vault.already_open";
    public const string VaultLocationExists = "vault.location_exists";
    public const string VaultLocationMissing = "vault.location_missing";
    public const string VaultConfigInvalid = "vault.config_invalid";

    public const string AccountNotFound = "account.not_found";

    public const string FrontmatterTooLarge = "frontmatter.too_large";
    public const string FrontmatterInvalidYaml = "frontmatter.invalid_yaml";
    public const string FrontmatterNotAMapping = "frontmatter.not_a_mapping";
}
