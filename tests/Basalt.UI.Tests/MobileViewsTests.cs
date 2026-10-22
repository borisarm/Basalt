using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Basalt.Domain.Notes;
using Basalt.Presentation.Common;
using Basalt.UI.Editor;
using Basalt.UI.Views;

namespace Basalt.UI.Tests;

public sealed class MobileViewsTests
{
    private static async Task SettleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Fact]
    [Trait("Spec", "F-014")]
    public Task MobileWorkspace_OpensTheSettingsPage() => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(new Dictionary<string, string>());
        var view = new MobileWorkspaceView { DataContext = v.Workspace };
        var window = new Window { Content = view, Width = 390, Height = 844 };
        window.Show();
        await SettleAsync();

        Click(view.FindControl<Button>("MoreTab")!);
        await SettleAsync();
        var sheet = view.FindControl<Border>("Sheet")!;
        sheet.IsVisible.Should().BeTrue();
        var settings = sheet.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Settings"));
        Click(settings);
        await SettleAsync();

        view.GetVisualDescendants().OfType<SettingsView>().Should().ContainSingle();
        window.Close();
    });

    [Fact]
    [Trait("Spec", "F-004.1")]
    public Task MobileWorkspace_ShowsTheExplorer_AndOpensTheNote() => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(new Dictionary<string, string> { ["A.md"] = "# A" });
        var view = new MobileWorkspaceView { DataContext = v.Workspace };
        var window = new Window { Content = view, Width = 390, Height = 844 };
        window.Show();
        await SettleAsync();

        var overlay = view.FindControl<Border>("PageOverlay")!;
        overlay.IsVisible.Should().BeTrue("the stack starts at the explorer");

        await v.Workspace.OpenAsync(new OpenRequest(VaultPath.Create("A.md").Value), TestContext.Current.CancellationToken);
        await SettleAsync();

        overlay.IsVisible.Should().BeFalse();
        var note = view.GetVisualDescendants().OfType<MobileNoteView>().Single();
        var editor = note.GetVisualDescendants().OfType<MarkdownTextBoxEditor>().Single();
        editor.GetText().Should().Be("# A");

        editor.Insert("[[");
        editor.GetText().Should().Be("# A[[");
        editor.Undo();
        editor.GetText().Should().Be("# A");

        window.Close();
    });
}
