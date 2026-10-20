using Basalt.Domain.Common;

namespace Basalt.Application.Ports.Security;

/// <summary>Error codes of the secure store (spec 03 §1.5).</summary>
public static class SecretErrors
{
    public const string StoreFailed = "secret.store_failed";
    public const string ReadFailed = "secret.read_failed";
    public const string DeleteFailed = "secret.delete_failed";
    public const string Unsupported = "secret.unsupported";
}

/// <summary>OS-protected storage for tokens (spec 03 §1.5, 06 §15). Values never go to logs or config files.</summary>
public interface ISecretStore
{
    Task<Result<Unit>> SetAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct);

    Task<Result<byte[]?>> GetAsync(string key, CancellationToken ct);

    Task<Result<Unit>> DeleteAsync(string key, CancellationToken ct);
}
