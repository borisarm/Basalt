using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;

using Avalonia.Threading;
using Avalonia.VisualTree;
using Basalt.Domain.Graph;
using Basalt.Domain.Notes;
using Basalt.Presentation.Panels;
using Basalt.Presentation.Workspace;
using Basalt.Testing.Fakes;
using Basalt.UI.Graph;
using Basalt.UI.Views;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Basalt.UI.Tests;

public sealed class GraphViewTests
{
    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static GraphModel Model() => new(
        [
            new GraphNode("A.md", GraphNodeKind.Note, "A", P("A.md")),
            new GraphNode("B.md", GraphNodeKind.Note, "B", P("B.md")),
            new GraphNode("C.md", GraphNodeKind.Note, "C", P("C.md")),
        ],
        [new GraphEdge(0, 1, GraphEdgeKind.Link), new GraphEdge(1, 2, GraphEdgeKind.Link)]);

    private static (Window Window, GraphCanvas Canvas, ForceLayout Layout) Host(GraphModel? graph = null)
    {
        var model = graph ?? Model();
        var layout = new ForceLayout(model, ForceSettings.Default, new FakeRandom(1).NextDouble);
        var canvas = new GraphCanvas { Graph = model, Layout = layout, AutoAdvance = false };
        return (new Window { Width = 400, Height = 400, Content = canvas }, canvas, layout);
    }

    private static Point InWindow(GraphCanvas canvas, Point local) =>
        canvas.TranslatePoint(local, TopLevel.GetTopLevel(canvas)!) ?? local;

    private static Point NodeCenter(GraphCanvas canvas, ForceLayout layout, int node)
    {
        var (x, y) = layout.PositionOf(node);
        return canvas.ToScreen(x, y);
    }

    private static int ColoredPixels(Window window)
    {
        using var frame = window.CaptureRenderedFrame();
        if (frame is null)
        {
            return 0;
        }

        using var buffer = frame.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        var colored = 0;
        for (var y = 0; y < buffer.Size.Height; y++)
        {
            for (var x = 0; x < buffer.Size.Width; x++)
            {
                var i = (y * buffer.RowBytes) + (x * 4);
                var max = Math.Max(bytes[i], Math.Max(bytes[i + 1], bytes[i + 2]));
                var min = Math.Min(bytes[i], Math.Min(bytes[i + 1], bytes[i + 2]));
                if (max - min > 30)
                {
                    colored++;
                }
            }
        }

        return colored;
    }

    [Fact]
    [Trait("Spec", "F-012")]
    public Task Canvas_FitsTheGraph_AndDrawsColouredNodes() => Headless.RunAsync(() =>
    {
        var (window, canvas, layout) = Host();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        canvas.Zoom.Should().BeGreaterThan(0);
        canvas.Bounds.Width.Should().BeGreaterThan(0);
        foreach (var node in Enumerable.Range(0, 3))
        {
            var p = NodeCenter(canvas, layout, node);
            p.X.Should().BeInRange(0, canvas.Bounds.Width);
            p.Y.Should().BeInRange(0, canvas.Bounds.Height);
        }

        ColoredPixels(window).Should().BeGreaterThan(50, "the nodes are drawn with their brush");

        canvas.AreLabelsVisible.Should().Be(canvas.Zoom >= GraphCanvas.LabelZoomThreshold);
        window.Close();
    });

    [Fact]
    [Trait("Spec", "F-012")]
    public Task Zoom_KeepsThePointUnderThePointer_AndPanShiftsTheView() => Headless.RunAsync(() =>
    {
        var (window, canvas, _) = Host();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var anchor = new Point(120, 90);
        var before = canvas.ToWorld(anchor);
        canvas.ZoomAt(anchor, 2);
        canvas.Zoom.Should().BeGreaterThan(1);
        var after = canvas.ToWorld(anchor);
        after.X.Should().BeApproximately(before.X, 0.01);
        after.Y.Should().BeApproximately(before.Y, 0.01);

        var world = canvas.ToWorld(new Point(10, 10));
        canvas.PanBy(new Vector(30, -12));
        canvas.ToScreen(world.X, world.Y).Should().Be(new Point(40, -2));

        window.Close();
    });

    [Fact]
    [Trait("Spec", "F-012")]
    public Task WheelZooms_AndDraggingTheBackgroundPans() => Headless.RunAsync(() =>
    {
        var (window, canvas, _) = Host();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var zoom = canvas.Zoom;
        window.MouseWheel(new Point(200, 200), new Vector(0, 120));
        Dispatcher.UIThread.RunJobs();
        canvas.Zoom.Should().BeGreaterThan(zoom);

        var world = canvas.ToWorld(new Point(10, 10));
        var before = canvas.ToScreen(world.X, world.Y);
        window.MouseDown(new Point(5, 5), MouseButton.Left);
        window.MouseMove(new Point(35, 15));
        window.MouseUp(new Point(35, 15), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var moved = canvas.ToScreen(world.X, world.Y);
        (moved.X - before.X).Should().BeApproximately(30, 0.5);
        (moved.Y - before.Y).Should().BeApproximately(10, 0.5);

        window.Close();
    });

    [Fact]
    [Trait("Spec", "F-012")]
    public Task DraggingANode_PinsIt_UntilRelease() => Headless.RunAsync(() =>
    {
        var (window, canvas, layout) = Host();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var start = NodeCenter(canvas, layout, 1);
        var target = start + new Vector(40, 25);
        window.MouseDown(InWindow(canvas, start), MouseButton.Left);
        window.MouseMove(InWindow(canvas, target));
        Dispatcher.UIThread.RunJobs();

        NodeCenter(canvas, layout, 1).X.Should().BeApproximately(target.X, 0.5);
        NodeCenter(canvas, layout, 1).Y.Should().BeApproximately(target.Y, 0.5);
        canvas.HoveredNode.Should().Be(1);

        window.MouseUp(InWindow(canvas, target), MouseButton.Left);
        window.Close();
    });

    [Fact]
    [Trait("Spec", "F-012")]
    public Task ClickingANode_ActivatesIt_AndCtrlOpensANewTab() => Headless.RunAsync(() =>
    {
        var (window, canvas, layout) = Host();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var activated = new List<GraphNodeActivatedEventArgs>();
        canvas.NodeActivated += (_, e) => activated.Add(e);

        var point = NodeCenter(canvas, layout, 1);
        window.MouseDown(InWindow(canvas, point), MouseButton.Left);
        window.MouseUp(InWindow(canvas, point), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        activated.Should().ContainSingle();
        activated[0].Node.Id.Should().Be("B.md");
        activated[0].NewTab.Should().BeFalse();

        var other = NodeCenter(canvas, layout, 2);
        window.MouseDown(InWindow(canvas, other), MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(InWindow(canvas, other), MouseButton.Left, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        activated.Should().HaveCount(2);
        activated[1].Node.Id.Should().Be("C.md");
        activated[1].NewTab.Should().BeTrue();

        window.Close();
    });

    [Fact]
    [Trait("Spec", "F-012")]
    public Task Hover_LightsTheNeighbours_AndDimsTheRest() => Headless.RunAsync(() =>
    {
        var (window, canvas, layout) = Host();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.MouseMove(InWindow(canvas, NodeCenter(canvas, layout, 1)));
        Dispatcher.UIThread.RunJobs();
        canvas.HoveredNode.Should().Be(1);
        canvas.IsNodeLit(0).Should().BeTrue("it is a neighbour");
        canvas.IsNodeLit(1).Should().BeTrue();
        canvas.IsNodeLit(2).Should().BeTrue("it is a neighbour");

        window.MouseMove(InWindow(canvas, NodeCenter(canvas, layout, 0)));
        Dispatcher.UIThread.RunJobs();
        canvas.HoveredNode.Should().Be(0);
        canvas.IsNodeLit(1).Should().BeTrue();
        canvas.IsNodeLit(2).Should().BeFalse("it is not connected to the hovered node");

        window.MouseMove(new Point(398, 398));
        Dispatcher.UIThread.RunJobs();
        canvas.HoveredNode.Should().Be(-1);
        canvas.IsNodeLit(2).Should().BeTrue("without a hovered node everything is lit");

        window.Close();
    });

    [Fact]
    [Trait("Spec", "F-012")]
    public async Task GraphTab_OpensFromTheRibbon_ShowsSettings_AndOpensNotes()
    {
        await Headless.RunAsync(async () =>
        {
            await using var v = await ComposedVault.CreateAsync(new Dictionary<string, string>
            {
                ["A.md"] = "enlaza [[B]]",
                ["B.md"] = "hola",
            });
            await SettleAsync();

            var button = v.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "GraphButton");
            Click(button);
            await SettleAsync();

            var tab = v.Workspace.Layout.ActiveGroup.ActiveTab.Should().BeOfType<GraphTabViewModel>().Subject;
            await tab.WhenRefreshedAsync();
            await SettleAsync();
            tab.Graph!.Nodes.Should().HaveCount(2);

            var view = v.Window.GetVisualDescendants().OfType<GraphTabView>().Single();
            view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)
                .Should().Contain(["Graph settings", "Orphan notes", "Gravity"]);
            var canvas = view.GetVisualDescendants().OfType<GraphCanvas>().Single();
            canvas.Graph.Should().NotBeNull();

            canvas.AutoAdvance = false;
            var node = tab.Graph.Nodes.Single(n => n.Id == "A.md");
            var point = NodeCenter(canvas, tab.Layout!, tab.Graph.IndexOf(node.Id));
            v.Window.MouseDown(InWindow(canvas, point), MouseButton.Left);
            v.Window.MouseUp(InWindow(canvas, point), MouseButton.Left);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (v.Workspace.ActiveFile != P("A.md") && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
                await SettleAsync();
            }

            v.Workspace.ActiveFile.Should().Be(P("A.md"));
        });
    }

    [Fact]
    [Trait("Spec", "F-012.1")]
    public async Task LocalGraphPanel_ShowsItsOwnCanvas()
    {
        await Headless.RunAsync(async () =>
        {
            await using var v = await ComposedVault.CreateAsync(new Dictionary<string, string>
            {
                ["A.md"] = "enlaza [[B]]",
                ["B.md"] = "hola",
            });
            var ct = TestContext.Current.CancellationToken;
            await v.Workspace.OpenAsync(new Presentation.Common.OpenRequest(P("A.md")), ct);
            v.Workspace.FocusPanel(PanelIds.LocalGraph);
            await v.Workspace.Panels!.LocalGraph.WhenRefreshedAsync();
            await SettleAsync();

            var view = v.Window.GetVisualDescendants().OfType<LocalGraphView>().Single();
            var canvas = view.GetVisualDescendants().OfType<GraphCanvas>().Single();
            canvas.Graph.Should().NotBeNull();
            view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)
                .Should().Contain(["Depth", "Direction"]);
        });
    }

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Button button) =>
        ((Avalonia.Automation.Provider.IInvokeProvider)Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(button)).Invoke();
}
