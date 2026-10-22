using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Basalt.Domain.Accounts;
using Basalt.Presentation.Panels;
using Basalt.Presentation.Settings;
using Basalt.Presentation.Workspace;
using Basalt.UI.Localization;

namespace Basalt.UI.Views;

/// <summary>iPhone workspace: explorer → note navigation, bottom bar and bottom sheets (spec 07 §4).</summary>
public partial class MobileWorkspaceView : UserControl
{
    private WorkspaceViewModel? _workspace;
    private SettingsViewModel? _settings;
    private TopLevel? _top;
    private SettingsView? _settingsView;
    private TaskCompletionSource<bool>? _signIn;

    public MobileWorkspaceView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Watch();
        AttachedToVisualTree += (_, _) =>
        {
            Watch();
            _top = TopLevel.GetTopLevel(this);
            if (_top is not null)
            {
                _top.BackRequested += OnBackRequested;
            }

            if (_workspace?.ActiveFile is null)
            {
                ShowExplorer();
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Unwatch();
            _settings?.Dispose();
            _settings = null;
            _settingsView = null;
            if (_top is not null)
            {
                _top.BackRequested -= OnBackRequested;
                _top = null;
            }
        };
    }

    private void Watch()
    {
        Unwatch();
        if (DataContext is WorkspaceViewModel workspace)
        {
            _workspace = workspace;
            workspace.PropertyChanged += OnWorkspaceChanged;
        }
    }

    private void Unwatch()
    {
        if (_workspace is not null)
        {
            _workspace.PropertyChanged -= OnWorkspaceChanged;
            _workspace = null;
        }
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.ActiveFile) && _workspace?.ActiveFile is not null)
        {
            PageOverlay.IsVisible = false;
            Sheet.IsVisible = false;
        }
    }

    private void OnShowExplorer(object? sender, RoutedEventArgs e) => ShowExplorer();

    private void ShowExplorer() => ShowPage(_workspace?.Panels?.Explorer, "Panel.files");

    private void OnShowSearch(object? sender, RoutedEventArgs e)
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        workspace.ShowPanelCommand.Execute(PanelIds.Search);
        ShowPage(workspace.PanelFor(PanelIds.Search), "Panel.search");
    }

    private void OnNewNote(object? sender, RoutedEventArgs e)
    {
        if (_workspace?.Panels?.Explorer is { } explorer)
        {
            _ = explorer.NewNoteCommand.ExecuteAsync(null);
        }
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (_signIn is not null)
        {
            _signIn.TrySetResult(false);
        }

        if (Sheet.IsVisible)
        {
            Sheet.IsVisible = false;
        }
        else if (PageOverlay.IsVisible)
        {
            OnPageBack(sender, e);
        }
        else
        {
            ShowExplorer();
        }

        e.Handled = true;
    }

    private void OnPageBack(object? sender, RoutedEventArgs e)
    {
        _signIn?.TrySetResult(false);
        PageOverlay.IsVisible = false;
        if (_workspace?.ActiveFile is null)
        {
            ShowExplorer();
        }
    }

    private void OnShowMore(object? sender, RoutedEventArgs e)
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        var menu = new StackPanel { Spacing = 4 };
        Add(menu, "Menu.QuickSwitcher", () =>
        {
            if (workspace.Panels?.QuickSwitcher is { } switcher)
            {
                ShowSheet(new QuickSwitcherView { DataContext = switcher }, "Menu.QuickSwitcher");
                _ = workspace.ShowQuickSwitcherCommand.ExecuteAsync(null);
            }
        });
        Add(menu, "Menu.CommandPalette", () =>
        {
            if (workspace.Panels?.CommandPalette is { } palette)
            {
                workspace.ShowCommandPaletteCommand.Execute(null);
                ShowSheet(new CommandPaletteView { DataContext = palette }, "Menu.CommandPalette");
            }
        });
        Add(menu, "Panel.bookmarks", () => ShowPanel(PanelIds.Bookmarks));
        Add(menu, "Panel.tags", () => ShowPanel(PanelIds.Tags));
        Add(menu, "Commands.OpenGraph", () => _ = workspace.OpenGraphCommand.ExecuteAsync(null));
        Add(menu, "Commands.OpenConflicts", () => _ = workspace.OpenConflictsCommand.ExecuteAsync(null));
        Add(menu, "Commands.OpenHistory", () => _ = workspace.OpenHistoryCommand.ExecuteAsync(null));
        Add(menu, "Settings.Title", ShowSettings);
        ShowSheet(menu, "Mobile.More");
    }

    private void OnShowPanels(object? sender, RoutedEventArgs e)
    {
        var menu = new StackPanel { Spacing = 4 };
        Add(menu, "Panel.backlinks", () => ShowPanel(PanelIds.Backlinks));
        Add(menu, "Panel.outgoing", () => ShowPanel(PanelIds.Outgoing));
        Add(menu, "Panel.outline", () => ShowPanel(PanelIds.Outline));
        Add(menu, "Panel.local-graph", () => ShowPanel(PanelIds.LocalGraph));
        Add(menu, "Panel.properties", () => ShowPanel(PanelIds.Properties));
        ShowSheet(menu, "Mobile.Panels");
    }

    private void ShowPanel(string panelId)
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        workspace.ShowPanelCommand.Execute(panelId);
        ShowPage(workspace.PanelFor(panelId), "Panel." + panelId);
    }

    private void ShowSettings()
    {
        if (_workspace is not { SettingsFactory: { } factory } workspace)
        {
            return;
        }

        _settings?.Dispose();
        _settings = factory(workspace.Vault) as SettingsViewModel;
        if (_settings is null)
        {
            return;
        }

        _settings.SignInPresenter = SignInAsync;
        _ = _settings.LoadAsync(CancellationToken.None);
        _settingsView = new SettingsView { DataContext = _settings };
        ShowPage(_settingsView, "Settings.Title");
    }

    private async Task<bool> SignInAsync(CancellationToken ct)
    {
        if (_workspace?.SignInFactory is not { } factory)
        {
            return false;
        }

        var viewModel = factory();
        var page = new MobileSignInView { DataContext = viewModel };
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _signIn = completion;
        void SignedIn(object? sender, GitHubAccount account)
        {
            completion.TrySetResult(true);
            ShowPage(_settingsView, "Settings.Title");
        }

        void Cancelled(object? sender, EventArgs e)
        {
            completion.TrySetResult(false);
            ShowPage(_settingsView, "Settings.Title");
        }

        viewModel.SignedIn += SignedIn;
        page.Cancelled += Cancelled;
        try
        {
            ShowPage(page, "SignIn.Title");
            _ = viewModel.StartAsync(GitHubHost.GitHubDotCom);
            return await completion.Task.WaitAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            viewModel.SignedIn -= SignedIn;
            page.Cancelled -= Cancelled;
            _signIn = null;
        }
    }

    private void OnCloseSheet(object? sender, RoutedEventArgs e) => Sheet.IsVisible = false;

    private void ShowPage(object? content, string titleKey)
    {
        Sheet.IsVisible = false;
        PageTitle.Text = LocalizationSource.Instance[titleKey];
        PageHost.Content = content;
        PageOverlay.IsVisible = true;
    }

    private void ShowSheet(object? content, string titleKey)
    {
        PageOverlay.IsVisible = false;
        SheetTitle.Text = LocalizationSource.Instance[titleKey];
        SheetHost.Content = content;
        Sheet.IsVisible = true;
    }

    private static void Add(StackPanel menu, string titleKey, Action action)
    {
        var button = new Button
        {
            Content = LocalizationSource.Instance[titleKey],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) => action();
        menu.Children.Add(button);
    }
}
