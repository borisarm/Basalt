using Basalt.Domain.Graph;

namespace Basalt.Domain.Tests.Graph;

public sealed class ForceLayoutTests
{
#pragma warning disable CA5394 // seeded, deterministic test randomness
    private static Func<double> Seeded(int seed)
    {
        var random = new Random(seed);
        return random.NextDouble;
    }
#pragma warning restore CA5394

    private static GraphModel Graph(int nodes, params (int, int)[] edges) => new(
        [.. Enumerable.Range(0, nodes).Select(i => new GraphNode($"n{i:D4}", GraphNodeKind.Note, $"n{i}", null))],
        [.. edges.Select(e => new GraphEdge(e.Item1, e.Item2, GraphEdgeKind.Link))]);

    private static double Distance(ForceLayout layout, int a, int b)
    {
        var (ax, ay) = layout.PositionOf(a);
        var (bx, by) = layout.PositionOf(b);
        return Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));
    }

    private static void Run(ForceLayout layout, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            layout.Step(1);
        }
    }

    [Fact]
    [Trait("Spec", "DOM-8")]
    public void SameSeed_SamePositions()
    {
        var graph = Graph(30, (0, 1), (1, 2), (2, 3), (5, 6));
        var first = new ForceLayout(graph, ForceSettings.Default, Seeded(7));
        var second = new ForceLayout(graph, ForceSettings.Default, Seeded(7));

        Run(first, 50);
        Run(second, 50);

        Enumerable.Range(0, 30).Select(first.PositionOf).Should().Equal(Enumerable.Range(0, 30).Select(second.PositionOf));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LinkedNodes_SettleCloserThanUnlinkedOnes(int seed)
    {
        // Analytic equilibria with the default forces (each node is d/2 from the origin):
        // spring: 500/d = 0.1(d - 60) + 0.02·d/2  →  d = 100;  no spring: 500/d = 0.02·d/2  →  d = √50000 ≈ 223.6.
        var linked = new ForceLayout(Graph(2, (0, 1)), ForceSettings.Default, Seeded(seed));
        var unlinked = new ForceLayout(Graph(2), ForceSettings.Default, Seeded(seed));

        Run(linked, 400);
        Run(unlinked, 400);

        Distance(linked, 0, 1).Should().BeApproximately(100, 1);
        Distance(unlinked, 0, 1).Should().BeApproximately(223.6, 1);
    }

    [Fact]
    public void SmallGraphs_BecomeStable()
    {
        var layout = new ForceLayout(Graph(10, (0, 1), (1, 2), (2, 0), (3, 4)), ForceSettings.Default, Seeded(3));
        layout.IsStable.Should().BeFalse();

        var steps = 0;
        while (!layout.IsStable && steps < 2000)
        {
            layout.Step(1);
            steps++;
        }

        layout.IsStable.Should().BeTrue();
        layout.KineticEnergy.Should().BeLessThan(ForceSettings.Default.StableEnergyPerNode * 10);
    }

    [Fact]
    public void BarnesHut_ApproximatesExactRepulsion()
    {
        var edges = Enumerable.Range(0, 599).Select(i => (i, i + 1)).ToArray();
        var graph = Graph(600, edges);
        var approximate = new ForceLayout(graph, ForceSettings.Default with { BarnesHutThreshold = 500, Theta = 0.5 }, Seeded(11));
        var exact = new ForceLayout(graph, ForceSettings.Default with { BarnesHutThreshold = int.MaxValue }, Seeded(11));

        approximate.Step(1);
        exact.Step(1);

        var drift = Enumerable.Range(0, 600).Max(i =>
        {
            var (ax, ay) = approximate.PositionOf(i);
            var (ex, ey) = exact.PositionOf(i);
            return Math.Sqrt(((ax - ex) * (ax - ex)) + ((ay - ey) * (ay - ey)));
        });
        var travelled = Enumerable.Range(0, 600).Average(i =>
        {
            var start = new ForceLayout(graph, ForceSettings.Default with { BarnesHutThreshold = int.MaxValue }, Seeded(11)).PositionOf(i);
            var (ex, ey) = exact.PositionOf(i);
            return Math.Sqrt(((start.X - ex) * (start.X - ex)) + ((start.Y - ey) * (start.Y - ey)));
        });
        drift.Should().BeLessThan(travelled * 0.5);
    }

    [Fact]
    public void LargeGraphs_UseBarnesHut_AndStayFinite()
    {
        var edges = Enumerable.Range(0, 1999).Select(i => (i, (i * 7 + 3) % 2000)).ToArray();
        var layout = new ForceLayout(Graph(2000, edges), ForceSettings.Default, Seeded(5));

        Run(layout, 5);

        Enumerable.Range(0, 2000).Select(layout.PositionOf).Should().OnlyContain(p => double.IsFinite(p.X) && double.IsFinite(p.Y));
    }

    [Fact]
    public void PinnedNodes_DoNotMove_UntilUnpinned()
    {
        var layout = new ForceLayout(Graph(3, (0, 1), (1, 2)), ForceSettings.Default, Seeded(2));

        layout.Pin(1, 500, -20);
        Run(layout, 20);

        layout.PositionOf(1).Should().Be((500, -20));
        layout.Unpin(1);
        layout.Step(1);
        layout.PositionOf(1).Should().NotBe((500, -20));
    }

    [Fact]
    public void CoincidentNodes_AreSeparated()
    {
        var layout = new ForceLayout(Graph(2), ForceSettings.Default, () => 0.5);

        Run(layout, 10);

        Distance(layout, 0, 1).Should().BeGreaterThan(0);
    }

    [Fact]
    public void EmptyGraph_IsStable()
    {
        var layout = new ForceLayout(GraphModel.Empty, ForceSettings.Default, Seeded(1));

        layout.Step(1);

        layout.Count.Should().Be(0);
        layout.IsStable.Should().BeTrue();
    }
}
