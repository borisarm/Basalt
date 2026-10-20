using Basalt.Application.Errors;
using Basalt.Domain.Common;
using Basalt.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Common;

/// <summary>The services every ViewModel needs.</summary>
public sealed record ViewModelServices(IUiDispatcher Dispatcher, INotificationService Notifications, ILocalizer Localizer, TimeProvider Time);

/// <summary>
/// Base of all ViewModels (spec 07 §2): operations run through <see cref="RunAsync{T}"/> show <see cref="IsBusy"/>
/// after 100 ms and offer <see cref="CancelCommand"/> after 2 s; failures are shown localized.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject, IDisposable
{
    public static readonly TimeSpan BusyDelay = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan CancelDelay = TimeSpan.FromSeconds(2);

    private readonly HashSet<CancellationTokenSource> _running = [];
    private int _busyCount;
    private int _cancellableCount;

    protected ViewModelBase(ViewModelServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
    }

    protected ViewModelServices Services { get; }

    protected ILocalizer Localizer => Services.Localizer;

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool CanCancel { get; private set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var source in _running.ToList())
            {
                source.Cancel();
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> with busy/cancel state; user cancellation returns <c>operation.cancelled</c> silently.</summary>
    protected async Task<Result<T>> RunAsync<T>(Func<CancellationToken, Task<Result<T>>> work, bool notifyErrors = true)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var source = new CancellationTokenSource();
        _running.Add(source);
        var busy = false;
        var cancellable = false;
        using var busyTimer = Services.Time.CreateTimer(_ => Services.Dispatcher.Post(() =>
        {
            if (!source.IsCancellationRequested && _running.Contains(source) && !busy)
            {
                busy = true;
                IsBusy = ++_busyCount > 0;
            }
        }), null, BusyDelay, Timeout.InfiniteTimeSpan);
        using var cancelTimer = Services.Time.CreateTimer(_ => Services.Dispatcher.Post(() =>
        {
            if (!source.IsCancellationRequested && _running.Contains(source) && !cancellable)
            {
                cancellable = true;
                CanCancel = ++_cancellableCount > 0;
            }
        }), null, CancelDelay, Timeout.InfiniteTimeSpan);

        Result<T> result;
        try
        {
            result = await work(source.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            result = new Error(ErrorCodes.OperationCancelled, "The operation was cancelled.");
        }
        finally
        {
            _running.Remove(source);
            if (busy)
            {
                IsBusy = --_busyCount > 0;
            }

            if (cancellable)
            {
                CanCancel = --_cancellableCount > 0;
            }
        }

        if (result.IsFailure && notifyErrors && result.Error.Code != ErrorCodes.OperationCancelled)
        {
            Services.Notifications.Show(new Notification(NotificationSeverity.Error, Localizer.Error(result.Error)));
        }

        return result;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        foreach (var source in _running.ToList())
        {
            source.Cancel();
        }
    }
}
