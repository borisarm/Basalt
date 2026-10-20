using Basalt.Domain.Vaults;

namespace Basalt.Application.Ports.Events;

/// <summary>An application event (spec 01 §4.3). Events of the same vault are delivered in publication order.</summary>
public interface IApplicationEvent
{
    /// <summary>The vault the event belongs to; null for application-wide events.</summary>
    VaultId? VaultId { get; }
}

/// <summary>In-process publish/subscribe with asynchronous, per-vault ordered delivery.</summary>
public interface IEventBus
{
    ValueTask PublishAsync<T>(T evt, CancellationToken ct)
        where T : IApplicationEvent;

    IDisposable Subscribe<T>(Func<T, CancellationToken, ValueTask> handler)
        where T : IApplicationEvent;
}
