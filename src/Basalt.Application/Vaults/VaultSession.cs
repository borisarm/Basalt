using System.Collections.Concurrent;
using Basalt.Application.Common;
using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Vaults;

/// <summary>
/// Everything that belongs to one open vault (spec 01 §4.4–4.5): its file system, shared settings and lock. Per-vault
/// services (index, metadata cache, sync engine) attach to the session as later tasks add them. See ADR-0012.
/// </summary>
public sealed class VaultSession : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _resources = [];

    public VaultSession(VaultId id, string name, IVaultFileSystem fileSystem, VaultSettings settings)
    {
        Id = id;
        Name = name;
        FileSystem = fileSystem;
        Settings = settings;
    }

    public VaultId Id { get; }

    public string Name { get; }

    public IVaultFileSystem FileSystem { get; }

    public VaultSettings Settings { get; private set; }

    /// <summary>Readers: queries; writer: content changes and the local-apply phase of sync.</summary>
    public AsyncReaderWriterLock Lock { get; } = new();

    public void UpdateSettings(VaultSettings settings) => Settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>Registers a per-vault resource disposed with the session.</summary>
    public void Own(IAsyncDisposable resource) => _resources.Add(resource);

    public async ValueTask DisposeAsync()
    {
        foreach (var resource in Enumerable.Reverse(_resources))
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }

        _resources.Clear();
        Lock.Dispose();
    }
}

/// <summary>The vaults currently open on this device (several on desktop, one on iOS).</summary>
public interface IVaultSessions
{
    IReadOnlyCollection<VaultSession> All { get; }

    /// <returns><c>vault.not_open</c> when the vault is not open.</returns>
    Result<VaultSession> Get(VaultId id);

    /// <returns><c>vault.already_open</c> when a session for that vault exists.</returns>
    Result<Unit> Add(VaultSession session);

    /// <summary>Closes and disposes the session; no-op when not open.</summary>
    Task CloseAsync(VaultId id);
}

/// <inheritdoc />
public sealed class VaultSessions : IVaultSessions, IAsyncDisposable
{
    private readonly ConcurrentDictionary<VaultId, VaultSession> _sessions = new();

    public IReadOnlyCollection<VaultSession> All => [.. _sessions.Values];

    public Result<VaultSession> Get(VaultId id) =>
        _sessions.TryGetValue(id, out var session)
            ? session
            : new Error(ErrorCodes.VaultNotOpen, "The vault is not open.");

    public Result<Unit> Add(VaultSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _sessions.TryAdd(session.Id, session)
            ? Result.Success()
            : new Error(ErrorCodes.VaultAlreadyOpen, "The vault is already open.");
    }

    public async Task CloseAsync(VaultId id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList())
        {
            await CloseAsync(id).ConfigureAwait(false);
        }
    }
}
