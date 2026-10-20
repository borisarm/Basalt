using Basalt.Application.Errors;
using Basalt.Domain.Common;
using Basalt.Presentation.Common;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Basalt.Presentation.Tests;

public sealed class ViewModelBaseTests
{
    private sealed class TestViewModel(ViewModelServices services) : ViewModelBase(services)
    {
        public Task<Result<T>> Run<T>(Func<CancellationToken, Task<Result<T>>> work, bool notifyErrors = true) => RunAsync(work, notifyErrors: notifyErrors);
    }

    private readonly FakeTimeProvider _time = new();
    private readonly FakeNotificationService _notifications = new();
    private readonly TestViewModel _vm;

    public ViewModelBaseTests() => _vm = new TestViewModel(new ViewModelServices(new ImmediateDispatcher(), _notifications, PresentationFakes.Localizer(), _time));

    [Fact]
    [Trait("Spec", "UI-2")]
    public async Task Busy_IsShownAfter100ms_AndCancellationAfter2s()
    {
        var gate = new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = _vm.Run(_ => gate.Task);

        _vm.IsBusy.Should().BeFalse();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _vm.IsBusy.Should().BeTrue();
        _vm.CanCancel.Should().BeFalse();
        _vm.CancelCommand.CanExecute(null).Should().BeFalse();
        _time.Advance(TimeSpan.FromMilliseconds(1900));
        _vm.CanCancel.Should().BeTrue();
        _vm.CancelCommand.CanExecute(null).Should().BeTrue();

        gate.SetResult(1);
        (await running).Value.Should().Be(1);
        _vm.IsBusy.Should().BeFalse();
        _vm.CanCancel.Should().BeFalse();
    }

    [Fact]
    public async Task QuickOperations_NeverShowBusy()
    {
        var changes = new List<string?>();
        _vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        await _vm.Run(_ => Task.FromResult(Result.Success(1)));
        _time.Advance(TimeSpan.FromSeconds(5));

        changes.Should().NotContain(nameof(ViewModelBase.IsBusy));
    }

    [Fact]
    public async Task Cancel_CancelsTheToken_AndIsNotReportedAsAnError()
    {
        var running = _vm.Run<int>(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });
        _time.Advance(TimeSpan.FromSeconds(2));

        _vm.CancelCommand.Execute(null);
        var result = await running;

        result.Error.Code.Should().Be(ErrorCodes.OperationCancelled);
        _notifications.Shown.Should().BeEmpty();
        _vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Errors_AreNotifiedLocalized_UnlessDisabled()
    {
        await _vm.Run(_ => Task.FromResult(Result.Failure<int>(new Error(ErrorCodes.EntryNotFound, "raw").With("path", "A.md"))));
        await _vm.Run(_ => Task.FromResult(Result.Failure<int>(new Error(ErrorCodes.EntryNotFound, "raw"))), notifyErrors: false);

        _notifications.Shown.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Severity = NotificationSeverity.Error, Message = "“A.md” does not exist." });
    }

    [Fact]
    public async Task Dispose_CancelsRunningOperations()
    {
        var running = _vm.Run<int>(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });

        _vm.Dispose();

        (await running).Error.Code.Should().Be(ErrorCodes.OperationCancelled);
    }

    [Fact]
    public async Task ForeignCancellations_Propagate()
    {
        using var other = new CancellationTokenSource();
        await other.CancelAsync();

        var run = () => _vm.Run<int>(_ => Task.FromCanceled<Result<int>>(other.Token));

        await run.Should().ThrowAsync<OperationCanceledException>();
        _vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task ErrorsWithoutMetadata_ShowAPlaceholder()
    {
        await _vm.Run(_ => Task.FromResult(Result.Failure<int>(new Error(ErrorCodes.EntryNotFound, "raw"))));

        _notifications.Shown.Single().Message.Should().Be("“…” does not exist.");
    }

    [Fact]
    public async Task ConcurrentOperations_KeepBusyUntilAllFinish()
    {
        var first = new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = _vm.Run(_ => first.Task);
        var b = _vm.Run(_ => second.Task);
        _time.Advance(TimeSpan.FromMilliseconds(150));

        first.SetResult(1);
        await a;
        _vm.IsBusy.Should().BeTrue();
        second.SetResult(2);
        await b;
        _vm.IsBusy.Should().BeFalse();
    }
}
