using Basalt.Domain.Common;

namespace Basalt.Application.Vaults;

/// <summary>
/// Extension point run when a vault opens (Open/Closed): per-vault components (index, metadata cache, sync engine)
/// attach themselves to the session. Initializers run in registration order; a failure aborts the opening.
/// </summary>
public interface IVaultSessionInitializer
{
    Task<Result<Unit>> InitializeAsync(VaultSession session, CancellationToken ct);
}
