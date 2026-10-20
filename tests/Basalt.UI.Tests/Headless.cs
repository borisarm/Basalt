using Avalonia.Headless;

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

    private static async Task DispatchAsync(Func<Task> action)
    {
        var ct = TestContext.Current.CancellationToken;
        await Gate.WaitAsync(ct);
        try
        {
            await Session.Value.Dispatch(action, ct);
        }
        finally
        {
            Gate.Release();
        }
    }
}
