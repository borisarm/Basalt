using System.Collections.Concurrent;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Common;

/// <summary>One <see cref="AsyncReaderWriterLock"/> per vault: content writes of a vault are serialised (spec 01 §4.4).</summary>
public sealed class VaultLocks : IDisposable
{
    private readonly ConcurrentDictionary<VaultId, AsyncReaderWriterLock> _locks = new();

    public Task<IDisposable> WriteAsync(VaultId vault, CancellationToken ct) => For(vault).WriteAsync(ct);

    public Task<IDisposable> ReadAsync(VaultId vault, CancellationToken ct) => For(vault).ReadAsync(ct);

    public void Dispose()
    {
        foreach (var rw in _locks.Values)
        {
            rw.Dispose();
        }
    }

    private AsyncReaderWriterLock For(VaultId vault) => _locks.GetOrAdd(vault, _ => new AsyncReaderWriterLock());
}
