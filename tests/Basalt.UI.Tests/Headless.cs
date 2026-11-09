using Avalonia.Headless;
using Avalonia.Threading;

namespace Basalt.UI.Tests;

/// <summary>
/// Runs test bodies on the Avalonia headless UI thread (ADR-0007: framework-agnostic Avalonia.Headless session,
/// because Avalonia.Headless.XUnit is not compatible with xUnit v3 4.x).
/// </summary>
public static class Headless
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder)));

    // The headless session initialises its isolated application lazily on the first Dispatch. Two test classes
    // dispatching at the same time race on the render loop, so bodies run one at a time (they share the UI thread
    // anyway, and this also keeps the app-wide culture stable between tests).
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static Task RunAsync(Action action) => DispatchAsync(() =>
    {
        action();
        return Task.CompletedTask;
    });

    public static Task RunAsync(Func<Task> action) => DispatchAsync(action);

    /// <summary>
    /// Pumps the UI thread until <paramref name="condition"/> holds or 10 s pass, for effects of asynchronous
    /// commands. A fixed delay before asserting is flaky on slow CI runners; the caller still asserts afterwards.
    /// </summary>
    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static async Task DispatchAsync(Func<Task> action)
    {
        var ct = TestContext.Current.CancellationToken;
        await Gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    // The session has no Func<Task> overload: a bare Func<Task> binds to Dispatch<TResult>(Func<TResult>)
                    // with TResult = Task and returns as soon as the body first yields, dropping the rest of it and its
                    // exceptions. Returning a value selects the Func<Task<TResult>> overload, which pumps the UI thread
                    // until the body completes.
                    await Session.Value.Dispatch(
                        async () =>
                        {
                            await action();
                            return true;
                        },
                        ct);
                    return;
                }
                catch (InvalidOperationException exception) when (attempt < 3 && IsDispatcherInitializationRace(exception))
                {
                    // Avalonia resets the UI-thread dispatcher before initialising each isolated application; if
                    // another thread claims it in that window the dispatch fails before the body runs. A fresh
                    // dispatch wins the race after the next reset (AvaloniaUI/Avalonia#22021).
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool IsDispatcherInitializationRace(Exception exception)
    {
        var stack = exception.StackTrace;
        return exception is InvalidOperationException
            && stack is not null
            && stack.Contains("EnsureIsolatedApplication", StringComparison.Ordinal)
            && stack.Contains("DefaultRenderLoop.Add", StringComparison.Ordinal);
    }
}
