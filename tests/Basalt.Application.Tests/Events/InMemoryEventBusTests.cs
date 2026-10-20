using Basalt.Application.Events;
using Basalt.Application.Ports.Events;
using Basalt.Domain.Vaults;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basalt.Application.Tests.Events;

public sealed class InMemoryEventBusTests
{
    private static readonly VaultId VaultA = new(Guid.Parse("00000000-0000-0000-0000-00000000000a"));
    private static readonly VaultId VaultB = new(Guid.Parse("00000000-0000-0000-0000-00000000000b"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record TestEvent(VaultId? VaultId, int N) : IApplicationEvent;

    private sealed record OtherEvent(VaultId? VaultId) : IApplicationEvent;

    [Fact]
    public async Task Subscribers_ReceiveEventsOfTheirTypeOnly()
    {
        await using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var received = new List<int>();
        using var subscription = bus.Subscribe<TestEvent>((e, _) =>
        {
            received.Add(e.N);
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new TestEvent(VaultA, 1), Ct);
        await bus.PublishAsync(new OtherEvent(VaultA), Ct);
        await bus.WaitForIdleAsync(Ct);

        received.Should().Equal(1);
    }

    [Fact]
    public async Task EventsOfAVault_AreDeliveredInOrder_EvenWithSlowHandlers()
    {
        await using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var received = new List<int>();
        using var subscription = bus.Subscribe<TestEvent>(async (e, ct) =>
        {
            await Task.Yield();
            lock (received)
            {
                received.Add(e.N);
            }
        });

        for (var i = 0; i < 200; i++)
        {
            await bus.PublishAsync(new TestEvent(VaultA, i), Ct);
        }

        await bus.WaitForIdleAsync(Ct);

        received.Should().Equal(Enumerable.Range(0, 200));
    }

    [Fact]
    public async Task ABlockedVault_DoesNotDelayAnotherVault()
    {
        await using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var release = new TaskCompletionSource();
        var deliveredB = new TaskCompletionSource();
        using var subscription = bus.Subscribe<TestEvent>(async (e, _) =>
        {
            if (e.VaultId == VaultA)
            {
                await release.Task;
            }
            else
            {
                deliveredB.TrySetResult();
            }
        });

        await bus.PublishAsync(new TestEvent(VaultA, 1), Ct);
        await bus.PublishAsync(new TestEvent(VaultB, 2), Ct);

        await deliveredB.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        release.SetResult();
        await bus.WaitForIdleAsync(Ct);
    }

    [Fact]
    public async Task AFailingSubscriber_DoesNotStopOthersOrLaterEvents()
    {
        await using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var received = new List<int>();
        using var failing = bus.Subscribe<TestEvent>((_, _) => throw new InvalidOperationException("boom"));
        using var healthy = bus.Subscribe<TestEvent>((e, _) =>
        {
            received.Add(e.N);
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new TestEvent(null, 1), Ct);
        await bus.PublishAsync(new TestEvent(null, 2), Ct);
        await bus.WaitForIdleAsync(Ct);

        received.Should().Equal(1, 2);
    }

    [Fact]
    public async Task Unsubscribe_StopsDelivery()
    {
        await using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var count = 0;
        var subscription = bus.Subscribe<TestEvent>((_, _) =>
        {
            count++;
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new TestEvent(VaultA, 1), Ct);
        await bus.WaitForIdleAsync(Ct);
        subscription.Dispose();
        subscription.Dispose();
        await bus.PublishAsync(new TestEvent(VaultA, 2), Ct);
        await bus.WaitForIdleAsync(Ct);

        count.Should().Be(1);
    }

    [Fact]
    public async Task Subscribers_ForBaseInterfaces_ReceiveDerivedEvents()
    {
        await using var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var received = 0;
        using var subscription = bus.Subscribe<IApplicationEvent>((_, _) =>
        {
            received++;
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new TestEvent(VaultA, 1), Ct);
        await bus.PublishAsync(new OtherEvent(null), Ct);
        await bus.WaitForIdleAsync(Ct);

        received.Should().Be(2);
    }

    [Fact]
    public async Task PublishAfterDispose_Throws()
    {
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        await bus.DisposeAsync();

        await FluentActions.Awaiting(() => bus.PublishAsync(new TestEvent(null, 1), Ct).AsTask()).Should().ThrowAsync<ObjectDisposedException>();
    }
}
