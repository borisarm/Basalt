using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Basalt.Application.Abstractions;
using Basalt.Application.Vaults;
using Basalt.Application.Workspace;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Localization;
using Basalt.Presentation.Workspace;
using Basalt.UI.Localization;
using Basalt.UI.Services;
using Basalt.UI.Views;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.UI.Tests;

public sealed class WorkspaceViewTests
{
    private sealed class TestTab(ViewModelServices services) : TabViewModel(services)
    {
        public override string Kind => "note";

        protected override Task<Result<Unit>> LoadAsync(OpenRequest request, CancellationToken ct)
        {
            Path = request.Path;
            Title = request.Path.NameWithoutExtension;
            return Task.FromResult(Result.Success());
        }
    }

    private static (Window Window, WorkspaceViewModel Workspace) Build()
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("en"));
        LocalizationSource.Instance.Attach(localizer);
        var services = new ViewModelServices(new AvaloniaUiDispatcher(), new NotificationService(new FakeTimeProvider()), localizer, new FakeTimeProvider());
        var recent = Substitute.For<ICommandHandler<RecordRecentFile, Unit>>();
        recent.HandleAsync(Arg.Any<RecordRecentFile>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var workspace = new WorkspaceViewModel(
            services,
            new VaultSessionInfo(new VaultId(Guid.NewGuid()), "Notas", "C:/v", VaultSettings.Default, null),
            [new DelegateTabFactory("note", _ => true, () => new TestTab(services))],
            new WorkspaceMessenger(),
            new WorkspaceUseCases(recent, Substitute.For<IQueryHandler<LoadWorkspace, WorkspaceState?>>(), Substitute.For<ICommandHandler<SaveWorkspace, Unit>>()));
        var window = new Window { Width = 1200, Height = 700, Content = new WorkspaceView { DataContext = workspace } };
        return (window, workspace);
    }

    private static T Named<T>(Control root, string name)
        where T : Control => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void Click(Button button) => ((Avalonia.Automation.Provider.IInvokeProvider)Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(button)).Invoke();

    [Fact]
    [Trait("Spec", "F-013")]
    public Task Tabs_SplitsAndClosing() => Headless.RunAsync(async () =>
    {
        var (window, workspace) = Build();
        window.Show();
        var ct = TestContext.Current.CancellationToken;
        await workspace.OpenAsync(new OpenRequest(VaultPath.Create("A.md").Value), ct);
        await workspace.OpenAsync(new OpenRequest(VaultPath.Create("B.md").Value, NewTab: true), ct);
        Dispatcher.UIThread.RunJobs();

        var titles = window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text).ToList();
        titles.Should().Contain(["A", "B"]);

        await workspace.Layout.SplitCommand.ExecuteAsync(SplitOrientation.Vertical);
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<ListBox>().Count(l => l.Name == "Tabs").Should().Be(2);

        var close = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("close-tab"));
        Click(close);
        Dispatcher.UIThread.RunJobs();
        workspace.Layout.Groups.Sum(g => g.Tabs.Count).Should().Be(2);
        window.Close();
    });

    [Fact]
    public Task Sidebars_ToggleAndSwitchPanels() => Headless.RunAsync(() =>
    {
        var (window, workspace) = Build();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Named<DockPanel>(window, "LeftSidebar").IsVisible.Should().BeTrue();
        Click(Named<Button>(window, "ToggleLeft"));
        Dispatcher.UIThread.RunJobs();
        workspace.LeftSidebar.IsOpen.Should().BeFalse("the ribbon button toggles the sidebar");
        Named<DockPanel>(window, "LeftSidebar").IsVisible.Should().BeFalse();
        Named<Grid>(window, "Columns").ColumnDefinitions[0].Width.Value.Should().Be(0);

        var search = window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Search"));
        Click(search);
        workspace.LeftSidebar.ActivePanel.Should().Be("search");
        window.Close();
    });
}
