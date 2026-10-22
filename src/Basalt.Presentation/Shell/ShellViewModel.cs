using Basalt.Application.Abstractions;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Diff;
using Basalt.Presentation.Vaults;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Shell;

/// <param name="MultipleWindows">Desktop: each vault in its own window (F-001.7). iOS: one vault at a time.</param>
public sealed record ShellOptions(bool MultipleWindows);

/// <summary>Window management (desktop), implemented by the UI layer.</summary>
public interface IWindowService
{
    void OpenVaultWindow(VaultId vault);

    /// <summary>A new window showing the vault picker.</summary>
    void OpenNewWindow();

    /// <summary>A window with the settings of the app and, when a vault is open, of that vault (F-014).</summary>
    void OpenSettings(VaultSessionInfo vault);

    /// <summary>The GitHub device-flow dialog (F-015.1). True when an account was signed in.</summary>
    Task<bool> ShowSignInAsync(CancellationToken ct);

    /// <summary>A window with a side-by-side diff (spec 07 §8).</summary>
    Task ShowDiffAsync(DiffRequest request, CancellationToken ct);

    /// <summary>The sync setup wizard of a vault (F-015.2, F-015.5). True when sync was configured.</summary>
    Task<bool> ShowConfigureSyncAsync(VaultSessionInfo vault, CancellationToken ct);
}

/// <summary>
/// Root of a window (desktop) or of the app (iOS): shows the vault picker until a vault is open, then its workspace.
/// Opening another vault goes to a new window on desktop and replaces the open one on iOS.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase, IVaultOpener
{
    private readonly ShellOptions _options;
    private readonly ICommandHandler<OpenVault, VaultSessionInfo> _open;
    private readonly ICommandHandler<CloseVault, Unit> _close;
    private readonly IWindowService _windows;
    private readonly Func<VaultSessionInfo, ViewModelBase> _workspaceFactory;

    public ShellViewModel(
        ViewModelServices services,
        ShellOptions options,
        ICommandHandler<OpenVault, VaultSessionInfo> open,
        ICommandHandler<CloseVault, Unit> close,
        IWindowService windows,
        Func<IVaultOpener, VaultPickerViewModel> pickerFactory,
        Func<VaultSessionInfo, ViewModelBase> workspaceFactory)
        : base(services)
    {
        ArgumentNullException.ThrowIfNull(pickerFactory);
        _options = options;
        _open = open;
        _close = close;
        _windows = windows;
        _workspaceFactory = workspaceFactory;
        Picker = pickerFactory(this);
        Content = Picker;
        Title = Localizer.Get("Shell.Title");
    }

    public VaultPickerViewModel Picker { get; }

    /// <summary>Raised after a vault opens in this shell (the app remembers it for "open last vault").</summary>
    public event EventHandler<VaultId>? VaultOpened;

    [ObservableProperty]
    public partial ViewModelBase Content { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseVaultCommand))]
    public partial VaultId? ActiveVault { get; private set; }

    /// <summary>Loads the picker and, for windows opened for a vault, opens it.</summary>
    public async Task InitializeAsync(VaultId? vault, CancellationToken ct)
    {
        await Picker.LoadCommand.ExecuteAsync(null).ConfigureAwait(true);
        if (vault is { } id)
        {
            await OpenAsync(id, ct).ConfigureAwait(true);
        }
    }

    /// <summary>Flushes the open workspace (edits + layout) before the app goes to the background.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        if (Content is IClosable closable)
        {
            await closable.CloseAsync(ct).ConfigureAwait(true);
        }
    }

    public async Task<Result<Unit>> OpenAsync(VaultId vault, CancellationToken ct)
    {
        if (ActiveVault == vault)
        {
            return Result.Success();
        }

        if (ActiveVault is not null)
        {
            if (_options.MultipleWindows)
            {
                _windows.OpenVaultWindow(vault);
                return Result.Success();
            }

            await CloseVaultAsync().ConfigureAwait(true);
        }

        var opened = await RunAsync(token => _open.HandleAsync(new OpenVault(vault), token), notifyErrors: false).ConfigureAwait(true);
        if (opened.IsFailure)
        {
            return opened.Error;
        }

        Content = _workspaceFactory(opened.Value);
        ActiveVault = vault;
        Title = Localizer.Format("Shell.TitleWithVault", opened.Value.Name);
        VaultOpened?.Invoke(this, vault);
        return Result.Success();
    }

    [RelayCommand]
    private void NewWindow() => _windows.OpenNewWindow();

    private bool HasVault() => ActiveVault is not null;

    [RelayCommand(CanExecute = nameof(HasVault))]
    private async Task CloseVaultAsync()
    {
        if (ActiveVault is not { } vault)
        {
            return;
        }

        // Unsaved edits and the layout are written before the vault's session goes away (F-003: never lose an edit).
        if (Content is IClosable closable)
        {
            await closable.CloseAsync(CancellationToken.None).ConfigureAwait(true);
        }

        await RunAsync(token => _close.HandleAsync(new CloseVault(vault), token)).ConfigureAwait(true);
        (Content as IDisposable)?.Dispose();
        Content = Picker;
        ActiveVault = null;
        Title = Localizer.Get("Shell.Title");
        await Picker.LoadCommand.ExecuteAsync(null).ConfigureAwait(true);
    }
}
