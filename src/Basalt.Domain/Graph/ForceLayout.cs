namespace Basalt.Domain.Graph;

/// <summary>Forces of the layout (F-012 "fuerzas configurables").</summary>
/// <param name="Repulsion">Charge between every pair of nodes (force = Repulsion / distance).</param>
/// <param name="LinkStrength">Spring constant along edges.</param>
/// <param name="LinkDistance">Rest length of the springs.</param>
/// <param name="Gravity">Pull towards the origin, proportional to the distance.</param>
/// <param name="Damping">Velocity kept after each step (0–1).</param>
/// <param name="Theta">Barnes-Hut opening criterion (smaller is more exact).</param>
/// <param name="BarnesHutThreshold">Above this many nodes, repulsion uses the quadtree approximation.</param>
/// <param name="StableEnergyPerNode">Kinetic energy per node under which the layout is stable.</param>
/// <param name="MaxSpeed">Speed limit per step, so that close initial positions cannot blow the layout up.</param>
public sealed record ForceSettings(
    double Repulsion = 500,
    double LinkStrength = 0.1,
    double LinkDistance = 60,
    double Gravity = 0.02,
    double Damping = 0.85,
    double Theta = 0.9,
    int BarnesHutThreshold = 500,
    double StableEnergyPerNode = 0.01,
    double MaxSpeed = 50)
{
    public static ForceSettings Default { get; } = new();
}

/// <summary>
/// Fruchterman-Reingold style force simulation (spec 02 §8): repulsion between all nodes (Barnes-Hut quadtree beyond
/// <see cref="ForceSettings.BarnesHutThreshold"/> nodes), springs along edges and gravity to the origin. Deterministic
/// for a given random source (only used for the initial positions); the UI drives the animation with <see cref="Step"/>.
/// </summary>
public sealed class ForceLayout
{
    private const double MinDistanceSquared = 1e-4;

    private readonly ForceSettings _settings;
    private readonly (int Source, int Target)[] _springs;
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly double[] _vx;
    private readonly double[] _vy;
    private readonly double[] _fx;
    private readonly double[] _fy;
    private readonly bool[] _pinned;
    private bool _stepped;

    public ForceLayout(GraphModel graph, ForceSettings settings, Func<double> nextDouble)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(nextDouble);
        _settings = settings;
        var n = graph.Nodes.Count;
        _x = new double[n];
        _y = new double[n];
        _vx = new double[n];
        _vy = new double[n];
        _fx = new double[n];
        _fy = new double[n];
        _pinned = new bool[n];
        _springs = [.. graph.Edges.Where(e => e.Source != e.Target).Select(e => (e.Source, e.Target))];

        // Uniform in a disc whose area grows with the number of nodes.
        var radius = settings.LinkDistance * Math.Sqrt(Math.Max(n, 1));
        for (var i = 0; i < n; i++)
        {
            var angle = 2 * Math.PI * nextDouble();
            var distance = radius * Math.Sqrt(nextDouble());
            _x[i] = distance * Math.Cos(angle);
            _y[i] = distance * Math.Sin(angle);
        }
    }

    public int Count => _x.Length;

    /// <summary>Sum of squared speeds of the free nodes after the last step.</summary>
    public double KineticEnergy { get; private set; }

    public bool IsStable => Count == 0 || (_stepped && KineticEnergy < _settings.StableEnergyPerNode * Count);

    public (double X, double Y) PositionOf(int node) => (_x[node], _y[node]);

    public void Step(double dt)
    {
        _stepped = true;
        Array.Clear(_fx);
        Array.Clear(_fy);
        if (Count > _settings.BarnesHutThreshold)
        {
            RepelBarnesHut();
        }
        else
        {
            RepelExactly();
        }

        foreach (var (source, target) in _springs)
        {
            var dx = _x[target] - _x[source];
            var dy = _y[target] - _y[source];
            var distance = Math.Sqrt(Math.Max((dx * dx) + (dy * dy), MinDistanceSquared));
            var force = _settings.LinkStrength * (distance - _settings.LinkDistance) / distance;
            _fx[source] += dx * force;
            _fy[source] += dy * force;
            _fx[target] -= dx * force;
            _fy[target] -= dy * force;
        }

        var energy = 0.0;
        for (var i = 0; i < Count; i++)
        {
            if (_pinned[i])
            {
                continue;
            }

            _fx[i] -= _x[i] * _settings.Gravity;
            _fy[i] -= _y[i] * _settings.Gravity;
            var vx = (_vx[i] + (_fx[i] * dt)) * _settings.Damping;
            var vy = (_vy[i] + (_fy[i] * dt)) * _settings.Damping;
            var speed = Math.Sqrt((vx * vx) + (vy * vy));
            if (speed > _settings.MaxSpeed)
            {
                vx *= _settings.MaxSpeed / speed;
                vy *= _settings.MaxSpeed / speed;
            }

            _vx[i] = vx;
            _vy[i] = vy;
            _x[i] += vx * dt;
            _y[i] += vy * dt;
            energy += (vx * vx) + (vy * vy);
        }

        KineticEnergy = energy;
    }

    /// <summary>Fixes a node (while the user drags it).</summary>
    public void Pin(int node, double x, double y)
    {
        _pinned[node] = true;
        _x[node] = x;
        _y[node] = y;
        _vx[node] = 0;
        _vy[node] = 0;
    }

    public void Unpin(int node) => _pinned[node] = false;

    private void RepelExactly()
    {
        for (var i = 0; i < Count; i++)
        {
            for (var j = i + 1; j < Count; j++)
            {
                var (fx, fy) = Repulsion(_x[i] - _x[j], _y[i] - _y[j], 1, i - j);
                _fx[i] += fx;
                _fy[i] += fy;
                _fx[j] -= fx;
                _fy[j] -= fy;
            }
        }
    }

    private void RepelBarnesHut()
    {
        var tree = QuadTree.Build(_x, _y);
        for (var i = 0; i < Count; i++)
        {
            var (fx, fy) = tree.ForceOn(i, _x[i], _y[i], _settings.Theta, Repulsion);
            _fx[i] += fx;
            _fy[i] += fy;
        }
    }

    /// <summary>Repulsion of a charge <paramref name="mass"/> at offset (dx, dy); coincident points separate along a fixed direction.</summary>
    private (double X, double Y) Repulsion(double dx, double dy, double mass, int salt)
    {
        var squared = (dx * dx) + (dy * dy);
        if (squared < MinDistanceSquared)
        {
            var angle = salt * 2.399963229728653; // golden angle: deterministic, different per pair
            dx = Math.Cos(angle) * 0.01;
            dy = Math.Sin(angle) * 0.01;
            squared = MinDistanceSquared;
        }

        var force = _settings.Repulsion * mass / squared;
        return (dx * force, dy * force);
    }

    /// <summary>Barnes-Hut quadtree: each cell keeps its body count and centre of mass.</summary>
    private sealed class QuadTree
    {
        private const int MaxDepth = 40;

        private readonly double[] _xs;
        private readonly double[] _ys;
        private readonly double _minX;
        private readonly double _minY;
        private readonly double _size;
        private readonly int _depth;
        private QuadTree[]? _children;
        private List<int>? _bodies;
        private double _massX;
        private double _massY;
        private int _mass;

        private QuadTree(double[] xs, double[] ys, double minX, double minY, double size, int depth)
        {
            _xs = xs;
            _ys = ys;
            _minX = minX;
            _minY = minY;
            _size = size;
            _depth = depth;
        }

        public delegate (double X, double Y) RepulsionFunction(double dx, double dy, double mass, int salt);

        public static QuadTree Build(double[] x, double[] y)
        {
            var minX = x.Min();
            var minY = y.Min();
            var size = Math.Max(Math.Max(x.Max() - minX, y.Max() - minY), 1) * 1.0001;
            var root = new QuadTree(x, y, minX, minY, size, 0);
            for (var i = 0; i < x.Length; i++)
            {
                root.Insert(i);
            }

            return root;
        }

        public (double X, double Y) ForceOn(int body, double x, double y, double theta, RepulsionFunction repulsion)
        {
            if (_mass == 0)
            {
                return (0, 0);
            }

            if (_children is null)
            {
                var fx = 0.0;
                var fy = 0.0;
                foreach (var other in _bodies!.Where(b => b != body))
                {
                    var (ox, oy) = repulsion(x - _xs[other], y - _ys[other], 1, body - other);
                    fx += ox;
                    fy += oy;
                }

                return (fx, fy);
            }

            var dx = x - (_massX / _mass);
            var dy = y - (_massY / _mass);
            var distance = Math.Sqrt((dx * dx) + (dy * dy));
            if (distance > 0 && _size / distance < theta)
            {
                return repulsion(dx, dy, _mass, body);
            }

            var totalX = 0.0;
            var totalY = 0.0;
            foreach (var child in _children)
            {
                var (cx, cy) = child.ForceOn(body, x, y, theta, repulsion);
                totalX += cx;
                totalY += cy;
            }

            return (totalX, totalY);
        }

        private void Insert(int body)
        {
            _mass++;
            _massX += _xs[body];
            _massY += _ys[body];
            if (_children is null)
            {
                _bodies ??= [];
                _bodies.Add(body);
                if (_bodies.Count > 1 && _depth < MaxDepth)
                {
                    Subdivide();
                }

                return;
            }

            ChildFor(_xs[body], _ys[body]).Insert(body);
        }

        private void Subdivide()
        {
            var half = _size / 2;
            _children =
            [
                new QuadTree(_xs, _ys, _minX, _minY, half, _depth + 1),
                new QuadTree(_xs, _ys, _minX + half, _minY, half, _depth + 1),
                new QuadTree(_xs, _ys, _minX, _minY + half, half, _depth + 1),
                new QuadTree(_xs, _ys, _minX + half, _minY + half, half, _depth + 1),
            ];
            foreach (var body in _bodies!)
            {
                ChildFor(_xs[body], _ys[body]).Insert(body);
            }

            _bodies = null;
        }

        private QuadTree ChildFor(double x, double y)
        {
            var half = _size / 2;
            var index = (x >= _minX + half ? 1 : 0) + (y >= _minY + half ? 2 : 0);
            return _children![index];
        }
    }
}
