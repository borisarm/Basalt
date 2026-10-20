using Basalt.Application.Abstractions;
using Basalt.Application.Common;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basalt.Application.Tests.Abstractions;

public sealed class DecoratorTests
{
    private static readonly VaultId Vault = new(Guid.Parse("00000000-0000-0000-0000-00000000000a"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Rename(VaultId VaultId, string Name) : ICommand<string>, IVaultRequest;

    private sealed class RecordingHandler(Func<Rename, Task<Result<string>>>? body = null) : ICommandHandler<Rename, string>
    {
        public int Calls { get; private set; }

        public Task<Result<string>> HandleAsync(Rename command, CancellationToken ct)
        {
            Calls++;
            return body?.Invoke(command) ?? Task.FromResult(Result.Success("renamed to " + command.Name));
        }
    }

    private sealed class NotEmpty : IValidator<Rename>
    {
        public Error? Validate(Rename request) => request.Name.Length == 0 ? new Error("test.empty", "Empty") : null;
    }

    [Fact]
    public async Task Validation_ShortCircuitsInvalidRequests()
    {
        var inner = new RecordingHandler();
        var handler = new ValidationHandlerDecorator<Rename, string>(inner, [new NotEmpty()]);

        (await handler.HandleAsync(new Rename(Vault, string.Empty), Ct)).Error.Code.Should().Be("test.empty");
        (await handler.HandleAsync(new Rename(Vault, "ok"), Ct)).Value.Should().Be("renamed to ok");
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Logging_PassesResultsThrough_AndNeverLogsRequestContent()
    {
        var logger = new CapturingLogger();
        var handler = new LoggingHandlerDecorator<Rename, string>(
            new RecordingHandler(_ => Task.FromResult(Result.Failure<string>(new Error("test.failed", "secret content")))), logger);

        var result = await handler.HandleAsync(new Rename(Vault, "SecretNoteName"), Ct);

        result.Error.Code.Should().Be("test.failed");
        logger.Messages.Should().Contain(m => m.Contains("Rename", StringComparison.Ordinal) && m.Contains("test.failed", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(m => m.Contains("SecretNoteName", StringComparison.Ordinal) || m.Contains("secret content", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VaultLock_SerialisesCommandsOfTheSameVault()
    {
        using var locks = new VaultLocks();
        var gate = new TaskCompletionSource();
        var firstStarted = new TaskCompletionSource();
        var inner = new RecordingHandler(async c =>
        {
            firstStarted.TrySetResult();
            await gate.Task;
            return c.Name;
        });
        var handler = new VaultLockDecorator<Rename, string>(inner, locks);

        var first = handler.HandleAsync(new Rename(Vault, "1"), Ct);
        await firstStarted.Task;
        var second = handler.HandleAsync(new Rename(Vault, "2"), Ct);
        for (var i = 0; i < 20; i++)
        {
            await Task.Yield();
        }

        inner.Calls.Should().Be(1, "the second command waits for the vault lock");
        gate.SetResult();
        await Task.WhenAll(first, second);
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task VaultLock_DoesNotSerialiseDifferentVaults()
    {
        using var locks = new VaultLocks();
        var gate = new TaskCompletionSource();
        var started = 0;
        var inner = new RecordingHandler(async c =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                gate.SetResult();
            }

            await gate.Task;
            return c.Name;
        });
        var handler = new VaultLockDecorator<Rename, string>(inner, locks);

        var a = handler.HandleAsync(new Rename(Vault, "a"), Ct);
        var b = handler.HandleAsync(new Rename(new VaultId(Guid.Parse("00000000-0000-0000-0000-00000000000b")), "b"), Ct);

        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    private sealed class CapturingLogger : ILogger<LoggingHandlerDecorator<Rename, string>>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    [Fact]
    public void NullLoggerIsAccepted()
    {
        var handler = new LoggingHandlerDecorator<Rename, string>(new RecordingHandler(), NullLogger<LoggingHandlerDecorator<Rename, string>>.Instance);

        handler.Should().NotBeNull();
    }
}
