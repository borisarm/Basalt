using System.Globalization;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;

namespace Basalt.Application.Ports.Platform;

/// <summary>The culture used for dates in templates and daily notes (UI language setting or system culture).</summary>
public interface ICultureProvider
{
    CultureInfo Current { get; }
}

/// <summary>The operating system's recycle bin (desktop, <c>DeleteMode.SystemTrash</c>).</summary>
public interface ISystemTrash
{
    /// <summary>False where there is no recycle bin; callers then use the vault's <c>.trash/</c> folder.</summary>
    bool IsAvailable { get; }

    /// <returns><c>fs.not_found</c>, <c>fs.trash_unavailable</c>, <c>fs.io</c>.</returns>
    Task<Result<Unit>> MoveToTrashAsync(string rootLocation, VaultPath path, CancellationToken ct);
}
