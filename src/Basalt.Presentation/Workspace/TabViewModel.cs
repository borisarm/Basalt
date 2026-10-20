using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Presentation.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Workspace;

/// <summary>
/// A tab of the central area (spec 07 §2). Each tab keeps its own back/forward history (F-013); <see cref="ShowAsync"/>
/// replaces its content, <see cref="NavigateAsync"/> does so recording history.
/// </summary>
public abstract partial class TabViewModel(ViewModelServices services) : ViewModelBase(services)
{
    private readonly Stack<OpenRequest> _back = new();
    private readonly Stack<OpenRequest> _forward = new();

    /// <summary>Stable kind used by workspace persistence (<c>note</c>, <c>graph</c>, <c>image</c>…).</summary>
    public abstract string Kind { get; }

    [ObservableProperty]
    public partial string Title { get; protected set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    [ObservableProperty]
    public partial bool IsDirty { get; protected set; }

    [ObservableProperty]
    public partial VaultPath? Path { get; protected set; }

    /// <summary>What the tab shows now (the request it was last shown with).</summary>
    public OpenRequest? Current { get; private set; }

    /// <summary>View state persisted with the workspace (scroll, mode…).</summary>
    public virtual string? ViewState => null;

    public virtual void RestoreViewState(string? state)
    {
    }

    /// <summary>Shows <paramref name="request"/> in this tab (after saving pending changes).</summary>
    public async Task<Result<Unit>> ShowAsync(OpenRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shown = await LoadAsync(request, ct).ConfigureAwait(true);
        if (shown.IsSuccess)
        {
            Current = request;
        }

        return shown;
    }

    /// <summary>Like <see cref="ShowAsync"/>, remembering where the tab was for <see cref="GoBackCommand"/>.</summary>
    public async Task<Result<Unit>> NavigateAsync(OpenRequest request, CancellationToken ct)
    {
        var previous = Current;
        var shown = await ShowAsync(request, ct).ConfigureAwait(true);
        if (shown.IsSuccess && previous is not null)
        {
            _back.Push(previous);
            _forward.Clear();
            NotifyHistory();
        }

        return shown;
    }

    /// <summary>Writes pending changes now.</summary>
    public virtual Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

    protected abstract Task<Result<Unit>> LoadAsync(OpenRequest request, CancellationToken ct);

    private bool CanGoBack() => _back.Count > 0;

    private bool CanGoForward() => _forward.Count > 0;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task GoBackAsync(CancellationToken ct) => MoveAsync(_back, _forward, ct);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task GoForwardAsync(CancellationToken ct) => MoveAsync(_forward, _back, ct);

    private async Task MoveAsync(Stack<OpenRequest> from, Stack<OpenRequest> to, CancellationToken ct)
    {
        if (from.Count == 0)
        {
            return;
        }

        var previous = Current;
        var target = from.Pop();
        if ((await ShowAsync(target, ct).ConfigureAwait(true)).IsSuccess && previous is not null)
        {
            to.Push(previous);
        }

        NotifyHistory();
    }

    private void NotifyHistory()
    {
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>An <see cref="ITabFactory"/> from delegates.</summary>
public sealed class DelegateTabFactory(string kind, Func<VaultPath, bool> canOpen, Func<TabViewModel> create) : ITabFactory
{
    public string Kind => kind;

    public bool CanOpen(VaultPath path) => canOpen(path);

    public TabViewModel Create() => create();
}
