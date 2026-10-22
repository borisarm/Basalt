using Avalonia.Controls;
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
