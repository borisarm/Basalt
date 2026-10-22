using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
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

    public MobileWorkspaceView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Watch();
        AttachedToVisualTree += (_, _) =>
        {
            Watch();
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

    private void OnPageBack(object? sender, RoutedEventArgs e)
    {
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

        _ = _settings.LoadAsync(CancellationToken.None);
        ShowPage(new SettingsView { DataContext = _settings }, "Settings.Title");
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
