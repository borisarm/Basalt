using Avalonia.Headless;

namespace Basalt.UI.Tests;

/// <summary>
/// Runs test bodies on the Avalonia headless UI thread (ADR-0007: framework-agnostic Avalonia.Headless session,
/// because Avalonia.Headless.XUnit is not compatible with xUnit v3 4.x).
/// </summary>
public static class Headless
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder)));

    public static Task RunAsync(Action action) =>
        Session.Value.Dispatch(action, TestContext.Current.CancellationToken);

    public static Task RunAsync(Func<Task> action) =>
        Session.Value.Dispatch(action, TestContext.Current.CancellationToken);
}
