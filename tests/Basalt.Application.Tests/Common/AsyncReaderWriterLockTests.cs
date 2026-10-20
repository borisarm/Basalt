using Basalt.Application.Common;

namespace Basalt.Application.Tests.Common;

public sealed class AsyncReaderWriterLockTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Readers_AreConcurrent()
    {
        using var rw = new AsyncReaderWriterLock();

        using var first = await rw.ReadAsync(Ct);
        var second = rw.ReadAsync(Ct);

        second.IsCompletedSuccessfully.Should().BeTrue();
        (await second).Dispose();
    }

    [Fact]
    public async Task Writer_WaitsForReaders_AndBlocksNewReaders()
    {
        using var rw = new AsyncReaderWriterLock();
        var reader = await rw.ReadAsync(Ct);

        var writer = rw.WriteAsync(Ct);
        writer.IsCompleted.Should().BeFalse();
        var lateReader = rw.ReadAsync(Ct);
        lateReader.IsCompleted.Should().BeFalse("writers are not starved by new readers");

        reader.Dispose();
        var writeLease = await writer;
        lateReader.IsCompleted.Should().BeFalse();
        writeLease.Dispose();
        (await lateReader).Dispose();
    }

    [Fact]
    public async Task Writers_AreExclusive()
    {
        using var rw = new AsyncReaderWriterLock();
        var first = await rw.WriteAsync(Ct);

        var second = rw.WriteAsync(Ct);
        second.IsCompleted.Should().BeFalse();

        first.Dispose();
        (await second).Dispose();
    }

    [Fact]
    public async Task Cancellation_WhileWaiting_ThrowsAndDoesNotTakeTheLock()
    {
        using var rw = new AsyncReaderWriterLock();
        var writer = await rw.WriteAsync(Ct);
        using var cts = new CancellationTokenSource();

        var waiting = rw.WriteAsync(cts.Token);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();
        writer.Dispose();
        (await rw.WriteAsync(Ct)).Dispose();
    }

    [Fact]
    public async Task QueuedReaders_AreGrantedTogetherAfterTheWriter()
    {
        using var rw = new AsyncReaderWriterLock();
        var writer = await rw.WriteAsync(Ct);
        var r1 = rw.ReadAsync(Ct);
        var r2 = rw.ReadAsync(Ct);
        var w2 = rw.WriteAsync(Ct);

        writer.Dispose();
        var lease1 = await r1;
        var lease2 = await r2;
        w2.IsCompleted.Should().BeFalse();

        lease1.Dispose();
        lease2.Dispose();
        (await w2).Dispose();
    }

    [Fact]
    public async Task CancelledReader_InTheQueue_DoesNotBlockOthers()
    {
        using var rw = new AsyncReaderWriterLock();
        var writer = await rw.WriteAsync(Ct);
        using var cts = new CancellationTokenSource();
        var cancelled = rw.ReadAsync(cts.Token);
        var next = rw.WriteAsync(Ct);

        await cts.CancelAsync();
        writer.Dispose();

        await FluentActions.Awaiting(() => cancelled).Should().ThrowAsync<OperationCanceledException>();
        (await next).Dispose();
    }

    [Fact]
    public async Task AlreadyCancelledToken_Throws()
    {
        using var rw = new AsyncReaderWriterLock();

        await FluentActions.Awaiting(() => rw.ReadAsync(new CancellationToken(true))).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Dispose_CancelsWaiters()
    {
        var rw = new AsyncReaderWriterLock();
        var writer = await rw.WriteAsync(Ct);
        var waiting = rw.ReadAsync(Ct);

        rw.Dispose();

        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();
        writer.Dispose();
    }

    [Fact]
    public async Task VaultLocks_ProvideReadAndWritePerVault()
    {
        using var locks = new VaultLocks();
        var vault = new Domain.Vaults.VaultId(Guid.Parse("00000000-0000-0000-0000-000000000001"));

        using (await locks.ReadAsync(vault, Ct))
        {
            (await locks.ReadAsync(vault, Ct)).Dispose();
        }

        (await locks.WriteAsync(vault, Ct)).Dispose();
    }

    [Fact]
    public async Task DisposingALeaseTwice_IsHarmless()
    {
        using var rw = new AsyncReaderWriterLock();
        var lease = await rw.WriteAsync(Ct);

        lease.Dispose();
        lease.Dispose();

        (await rw.WriteAsync(Ct)).Dispose();
    }
}
