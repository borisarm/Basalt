using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Basalt.Application.Commands;
using Basalt.Domain.Notes;
using Basalt.Presentation.Common;
using Basalt.Presentation.Panels;
using Basalt.Presentation.Workspace;
using Basalt.UI.Input;
using Basalt.UI.Reading;
using Basalt.UI.Views;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Basalt.UI.Tests;

public sealed class PanelViewsTests
{
    private static readonly Dictionary<string, string> Files = new()
    {
        ["A.md"] = "# A\nver [[B]] #proyecto\n## Detalle\nmás",
        ["B.md"] = "# B\ntexto que menciona A",
        ["Carpeta/C.md"] = "---\nestado: activo\n---\nC",
    };

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static List<string?> Texts(Control root) => [.. root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)];

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        Dispatcher.UIThread.RunJobs();
    }

    // Pressed with nothing focused, as right after a vault opens: the shortcuts must still reach the workspace.
    private static void Press(ComposedVault v, Key key, RawInputModifiers modifiers) =>
        v.Window.KeyPress(key, modifiers, PhysicalKey.None, null);

    public static TheoryData<Key, KeyModifiers, string?> Keys => new()
    {
        { Key.O, KeyModifiers.Control, "Ctrl+O" },
        { Key.F, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+F" },
        { Key.Left, KeyModifiers.Alt, "Alt+Left" },
        { Key.OemOpenBrackets, KeyModifiers.Meta, "Win+[" },
        { Key.OemComma, KeyModifiers.Control, "Ctrl+," },
        { Key.D1, KeyModifiers.Control, "Ctrl+1" },
        { Key.F2, KeyModifiers.None, "F2" },
        { Key.LeftCtrl, KeyModifiers.Control, null },
    };

    [Theory]
    [MemberData(nameof(Keys))]
    public void GestureMapper_TranslatesKeys(Key key, KeyModifiers modifiers, string? expected) =>
        GestureMapper.From(key, modifiers)?.ToString(CommandPlatform.Windows).Should().Be(expected);

    [Fact]
    [Trait("Spec", "F-002")]
    public Task Explorer_ShowsTheTree_AndOpensNotes() => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(Files);
        var explorer = v.Workspace.Panels!.Explorer;
        await explorer.RefreshCommand.ExecuteAsync(null);
        await SettleAsync();

        var view = v.Window.GetVisualDescendants().OfType<FileExplorerView>().Single();
        Texts(view).Should().Contain(["Carpeta", "A", "B"]);

        explorer.Selected = explorer.Roots.Single(n => n.Name == "A");
        await explorer.OpenCommand.ExecuteAsync(explorer.Selected);
        await SettleAsync();
        v.Workspace.ActiveFile.Should().Be(P("A.md"));
    });

    [Fact]
    [Trait("Spec", "F-008")]
    [Trait("Spec", "NFR-040")]
    public Task QuickSwitcher_OpensWithItsShortcut_AndOpensTheChosenNote() => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(Files);
        await SettleAsync();

        Press(v, Key.O, RawInputModifiers.Control);
        await SettleAsync();
        var overlay = v.Window.GetVisualDescendants().OfType<QuickSwitcherView>().Single();
        overlay.IsVisible.Should().BeTrue();

        var switcher = v.Workspace.Panels!.QuickSwitcher;
        switcher.Text = "B";
        await switcher.WhenUpdatedAsync();
        await SettleAsync();
        Texts(overlay).Should().Contain("B");

        var input = overlay.FindControl<TextBox>("Input")!;
        input.Focus();
        v.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await SettleAsync();
        overlay.IsVisible.Should().BeFalse();
        v.Workspace.ActiveFile.Should().Be(P("B.md"));
    });

    [Fact]
    [Trait("Spec", "F-008.2")]
    [Trait("Spec", "NFR-040")]
    public Task CommandPalette_ListsCommandsWithShortcuts() => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(Files);
        await SettleAsync();

        Press(v, Key.P, RawInputModifiers.Control);
        await SettleAsync();

        var palette = v.Window.GetVisualDescendants().OfType<CommandPaletteView>().Single();
        palette.IsVisible.Should().BeTrue();
        Texts(palette).Should().Contain(["New note", "Ctrl+N"]);

        // The list is virtualized: filter to bring a command beyond the first rows into view.
        v.Workspace.Panels!.CommandPalette.Text = "Quick";
        await SettleAsync();
        Texts(palette).Should().Contain(["Quick switcher…", "Ctrl+O"]);

        palette.FindControl<TextBox>("Input")!.Focus();
        v.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await SettleAsync();
        palette.IsVisible.Should().BeFalse();
    });

    [Fact]
    [Trait("Spec", "F-007")]
    public Task Search_ShowsResultsPerNote() => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(Files);
        Press(v, Key.F, RawInputModifiers.Control | RawInputModifiers.Shift);
        await SettleAsync();
        var view = v.Window.GetVisualDescendants().OfType<SearchPanelView>().Single();

        var search = v.Workspace.Panels!.Search;
        search.Query = "menciona";
        await Task.Delay(SearchPanelViewModel.Debounce * 2, TestContext.Current.CancellationToken);
        await search.WhenSearchedAsync();
        await SettleAsync();

        search.Results.Select(r => r.Title).Should().Equal("B");
        Texts(view).Should().Contain("B");
    });

    [Fact]
    [Trait("Spec", "F-005")]
    public Task NotePanels_FollowTheActiveNote() => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(Files);
        var ct = TestContext.Current.CancellationToken;
        await v.Workspace.OpenAsync(new OpenRequest(P("B.md")), ct);
        v.Workspace.FocusPanel(PanelIds.Backlinks);
        await v.Workspace.Panels!.Backlinks.WhenRefreshedAsync();
        await SettleAsync();
        var backlinks = v.Window.GetVisualDescendants().OfType<BacklinksView>().Single();
        Texts(backlinks).Should().Contain(["Linked mentions", "A", "Unlinked mentions"]);

        await v.Workspace.OpenAsync(new OpenRequest(P("A.md")), ct);
        v.Workspace.FocusPanel(PanelIds.Outline);
        await v.Workspace.Panels.Outline.WhenRefreshedAsync();
        await SettleAsync();
        Texts(v.Window.GetVisualDescendants().OfType<OutlineView>().Single()).Should().Contain(["A", "Detalle"]);

        v.Workspace.FocusPanel(PanelIds.Outgoing);
        await v.Workspace.Panels.Outgoing.WhenRefreshedAsync();
        await SettleAsync();
        Texts(v.Window.GetVisualDescendants().OfType<OutgoingLinksView>().Single()).Should().Contain("B");

        await v.Workspace.OpenAsync(new OpenRequest(P("Carpeta/C.md")), ct);
        v.Workspace.FocusPanel(PanelIds.Properties);
        await v.Workspace.Panels.Properties.WhenRefreshedAsync();
        await SettleAsync();
        var properties = v.Window.GetVisualDescendants().OfType<PropertiesView>().Single();
        Texts(properties).Should().Contain("estado");
        properties.GetVisualDescendants().OfType<TextBox>().Single().Text.Should().Be("activo");
    });

    [Fact]
    [Trait("Spec", "F-006")]
    public Task Tags_ShowTheHierarchy() => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(Files);
        v.Workspace.FocusPanel(PanelIds.Tags);
        await v.Workspace.Panels!.Tags.RefreshCommand.ExecuteAsync(null);
        await SettleAsync();

        Texts(v.Window.GetVisualDescendants().OfType<TagsPanelView>().Single()).Should().Contain(["proyecto", "1"]);
    });

    [Fact]
    [Trait("Spec", "F-011")]
    public Task Explorer_CreatesANote_AndOpensIt() => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(Files);
        var explorer = v.Workspace.Panels!.Explorer;
        await explorer.RefreshCommand.ExecuteAsync(null);
        await SettleAsync();

        var view = v.Window.GetVisualDescendants().OfType<FileExplorerView>().Single();
        var newNote = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NewNote");
        ((IInvokeProvider)ControlAutomationPeer.CreatePeerForElement(newNote)).Invoke();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await SettleAsync();

        v.Workspace.ActiveFile.Should().Be(P("Untitled.md"));
        File.Exists(Path.Combine(v.RootLocation, "Untitled.md")).Should().BeTrue();
    });

    [Fact]
    [Trait("Spec", "F-003")]
    public Task ReadingMode_TogglesWithTheShortcut() => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(Files);
        await v.Workspace.OpenAsync(new OpenRequest(P("A.md")), TestContext.Current.CancellationToken);
        await SettleAsync();

        Press(v, Key.E, RawInputModifiers.Control);
        await SettleAsync();
        var tab = v.Workspace.Layout.ActiveGroup.ActiveTab.Should().BeOfType<NoteTabViewModel>().Subject;
        tab.IsReadingMode.Should().BeTrue();
        v.Window.GetVisualDescendants().OfType<RenderDocumentView>().Single().IsVisible.Should().BeTrue();

        Press(v, Key.E, RawInputModifiers.Control);
        await SettleAsync();
        tab.IsReadingMode.Should().BeFalse();
        v.Window.GetVisualDescendants().OfType<RenderDocumentView>().Single().IsVisible.Should().BeFalse();
    });
}
