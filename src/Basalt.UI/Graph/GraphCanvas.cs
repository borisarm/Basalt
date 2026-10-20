using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Basalt.Domain.Graph;

namespace Basalt.UI.Graph;

/// <summary>The node the user activated (clicked) in the graph.</summary>
public sealed record GraphNodeActivatedEventArgs(GraphNode Node, bool NewTab);

/// <summary>
/// The graph view (spec 07 §7): draws the nodes and edges of a <see cref="GraphModel"/> with a
/// <see cref="DrawingContext"/> in <see cref="Render"/> (no control per node). The <see cref="ForceLayout"/> simulation
/// runs on a background thread until it is stable, writing a double-buffered snapshot of the positions that the view
/// reads while it invalidates itself every frame. Mouse wheel zooms, dragging the background pans, dragging a node pins
/// it while dragged, hovering lights the neighbours and dims the rest, and clicking a node raises <see cref="NodeActivated"/>.
/// </summary>
public sealed class GraphCanvas : Control, IDisposable
{
    /// <summary>Zoom from which the node labels are drawn.</summary>
    public const double LabelZoomThreshold = 0.6;

    public static readonly StyledProperty<GraphModel?> GraphProperty =
        AvaloniaProperty.Register<GraphCanvas, GraphModel?>(nameof(Graph));

    public static readonly StyledProperty<ForceLayout?> LayoutProperty =
        AvaloniaProperty.Register<GraphCanvas, ForceLayout?>(nameof(Layout));

    /// <summary>Runs the simulation while attached; tests turn it off and call <see cref="Advance"/>.</summary>
    public static readonly StyledProperty<bool> AutoAdvanceProperty =
        AvaloniaProperty.Register<GraphCanvas, bool>(nameof(AutoAdvance), defaultValue: true);

    private const double NodeRadius = 7;
    private const double HitPadding = 4;
    private const double DragThreshold = 2;
    private const double MinZoom = 0.05;
    private const double MaxZoom = 8;
    private const double FitMargin = 40;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    private static readonly string[] GroupBrushes =
    [
        "BasaltGraphGroup0Brush",
        "BasaltGraphGroup1Brush",
        "BasaltGraphGroup2Brush",
        "BasaltGraphGroup3Brush",
    ];

    private readonly Lock _gate = new();
    private readonly Queue<Action<ForceLayout>> _pending = new();
    private readonly HashSet<int> _neighbours = [];
    private (double X, double Y)[] _positions = [];
    private (double X, double Y)[] _read = [];
    private double _zoom = 1;
    private Point _pan;
    private bool _fitted;
    private int _hovered = -1;
    private int? _dragNode;
    private Point _dragStart;
    private bool _dragMoved;
    private bool _panning;
    private Point _last;
    private CancellationTokenSource? _simulation;
    private DispatcherTimer? _frameTimer;

    static GraphCanvas()
    {
        AffectsRender<GraphCanvas>(GraphProperty, LayoutProperty);
        BoundsProperty.Changed.AddClassHandler<GraphCanvas>((canvas, _) => canvas.OnBoundsChanged());
        LayoutProperty.Changed.AddClassHandler<GraphCanvas>((canvas, _) => canvas.OnLayoutChanged());
        AutoAdvanceProperty.Changed.AddClassHandler<GraphCanvas>((canvas, _) => canvas.OnAutoAdvanceChanged());
    }

    /// <summary>Raised when a node is clicked; <c>NewTab</c> when Ctrl/Cmd was held.</summary>
    public event EventHandler<GraphNodeActivatedEventArgs>? NodeActivated;

    public GraphModel? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    public ForceLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public bool AutoAdvance
    {
        get => GetValue(AutoAdvanceProperty);
        set => SetValue(AutoAdvanceProperty, value);
    }

    /// <summary>Current magnification (1 = world units are screen pixels).</summary>
    public double Zoom => _zoom;

    /// <summary>Labels are drawn from <see cref="LabelZoomThreshold"/> up.</summary>
    public bool AreLabelsVisible => _zoom >= LabelZoomThreshold;

    /// <summary>The node under the pointer, or -1.</summary>
    public int HoveredNode => _hovered;

    public Point ToScreen(double x, double y) => new((x * _zoom) + _pan.X, (y * _zoom) + _pan.Y);

    public Point ToWorld(Point screen) => new((screen.X - _pan.X) / _zoom, (screen.Y - _pan.Y) / _zoom);

    /// <summary>Zooms keeping the world point under <paramref name="screen"/> where it is.</summary>
    public void ZoomAt(Point screen, double factor)
    {
        var world = ToWorld(screen);
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        _pan = new Point(screen.X - (world.X * _zoom), screen.Y - (world.Y * _zoom));
        InvalidateVisual();
    }

    public void PanBy(Vector delta)
    {
        _pan = new Point(_pan.X + delta.X, _pan.Y + delta.Y);
        InvalidateVisual();
    }

    /// <summary>Frames the whole graph.</summary>
    public void Fit()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        (double X, double Y)[] positions;
        lock (_gate)
        {
            positions = _positions;
        }

        if (positions.Length == 0)
        {
            _zoom = 1;
            _pan = new Point(Bounds.Width / 2, Bounds.Height / 2);
            _fitted = true;
            InvalidateVisual();
            return;
        }

        var minX = positions.Min(p => p.X);
        var minY = positions.Min(p => p.Y);
        var width = Math.Max(positions.Max(p => p.X) - minX, 1);
        var height = Math.Max(positions.Max(p => p.Y) - minY, 1);
        _zoom = Math.Clamp(
            Math.Min((Bounds.Width - (2 * FitMargin)) / width, (Bounds.Height - (2 * FitMargin)) / height),
            MinZoom,
            MaxZoom);
        _pan = new Point(
            (Bounds.Width / 2) - ((minX + (width / 2)) * _zoom),
            (Bounds.Height / 2) - ((minY + (height / 2)) * _zoom));
        _fitted = true;
        InvalidateVisual();
    }

    /// <summary>The index of the node whose circle is under <paramref name="screen"/>, or -1.</summary>
    public int NodeAt(Point screen)
    {
        lock (_gate)
        {
            for (var i = _positions.Length - 1; i >= 0; i--)
            {
                var centre = ToScreen(_positions[i].X, _positions[i].Y);
                var dx = centre.X - screen.X;
                var dy = centre.Y - screen.Y;
                if ((dx * dx) + (dy * dy) <= (NodeRadius + HitPadding) * (NodeRadius + HitPadding))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>With a hovered node only it and its neighbours stay lit.</summary>
    public bool IsNodeLit(int node) => _hovered < 0 || node == _hovered || _neighbours.Contains(node);

    /// <summary>One simulation step (tests drive the animation with it when <see cref="AutoAdvance"/> is off).</summary>
    public void Advance(double dt)
    {
        lock (_gate)
        {
            if (Layout is not { } layout)
            {
                return;
            }

            Drain(layout);
            layout.Step(dt);
            Capture(layout);
        }

        InvalidateVisual();
    }

    /// <summary>Raises <see cref="NodeActivated"/> for <paramref name="node"/>.</summary>
    public void Activate(int node, KeyModifiers modifiers = KeyModifiers.None)
    {
        if (Graph is not { } graph || node < 0 || node >= graph.Nodes.Count)
        {
            return;
        }

        var newTab = (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        NodeActivated?.Invoke(this, new GraphNodeActivatedEventArgs(graph.Nodes[node], newTab));
    }

    /// <summary>Stops the simulation (the control is disposed with its visual tree).</summary>
    public void Dispose()
    {
        StopSimulation();
        GC.SuppressFinalize(this);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        base.Render(context);
        context.FillRectangle(Brush("BasaltBackgroundBrush") ?? Brushes.Transparent, new Rect(Bounds.Size));
        if (Graph is not { } graph)
        {
            return;
        }

        var positions = Snapshot();
        if (positions.Length == 0)
        {
            return;
        }

        DrawEdges(context, graph, positions);
        DrawNodes(context, graph, positions);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        StartSimulation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopSimulation();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        _last = point;
        _dragStart = point;
        _dragMoved = false;
        var hit = NodeAt(point);
        if (hit >= 0)
        {
            _dragNode = hit;
            e.Pointer.Capture(this);
            var world = ToWorld(point);
            Apply(layout => layout.Pin(hit, world.X, world.Y));
            SetHovered(hit);
        }
        else
        {
            _panning = true;
            e.Pointer.Capture(this);
            SetHovered(-1);
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_dragNode is { } node)
        {
            var dragX = point.X - _dragStart.X;
            var dragY = point.Y - _dragStart.Y;
            if ((dragX * dragX) + (dragY * dragY) > DragThreshold * DragThreshold)
            {
                _dragMoved = true;
            }

            var world = ToWorld(point);
            Apply(layout => layout.Pin(node, world.X, world.Y));
            lock (_gate)
            {
                if (node < _positions.Length)
                {
                    _positions[node] = (world.X, world.Y);
                }
            }

            InvalidateVisual();
            e.Handled = true;
        }
        else if (_panning)
        {
            PanBy(point - _last);
            e.Handled = true;
        }
        else
        {
            SetHovered(NodeAt(point));
        }

        _last = point;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragNode is { } node)
        {
            Apply(layout => layout.Unpin(node));
            if (!_dragMoved)
            {
                Activate(node, e.KeyModifiers);
            }

            _dragNode = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
        else if (_panning)
        {
            _panning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAt(e.GetPosition(this), Math.Pow(1.1, e.Delta.Y / 120));
        e.Handled = true;
    }

    private void OnBoundsChanged()
    {
        if (!_fitted)
        {
            Fit();
        }
    }

    private void OnLayoutChanged()
    {
        lock (_gate)
        {
            _pending.Clear();
            var layout = Layout;
            var count = layout?.Count ?? 0;
            _positions = new (double X, double Y)[count];
            for (var i = 0; i < count; i++)
            {
                _positions[i] = layout!.PositionOf(i);
            }
        }

        _hovered = -1;
        _neighbours.Clear();
        _dragNode = null;
        _panning = false;
        _fitted = false;
        Fit();
        StartSimulation();
        InvalidateVisual();
    }

    private void OnAutoAdvanceChanged()
    {
        if (AutoAdvance)
        {
            StartSimulation();
        }
        else
        {
            StopSimulation();
        }
    }

    private void StartSimulation()
    {
        StopSimulation();
        if (!AutoAdvance || Layout is null || TopLevel.GetTopLevel(this) is null)
        {
            return;
        }

        _simulation = new CancellationTokenSource();
        var token = _simulation.Token;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = FrameInterval };
        _frameTimer = timer;
        timer.Tick += (sender, _) =>
        {
            if (token.IsCancellationRequested)
            {
                (sender as DispatcherTimer)?.Stop();
                return;
            }

            InvalidateVisual();
        };
        timer.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var stable = false;
                    lock (_gate)
                    {
                        if (Layout is not { } layout)
                        {
                            break;
                        }

                        Drain(layout);
                        layout.Step(1.0 / 60);
                        Capture(layout);
                        stable = layout.IsStable;
                    }

                    if (stable)
                    {
                        break;
                    }

                    await Task.Delay(FrameInterval, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    timer.Stop();
                    InvalidateVisual();
                });
            }
        }, token);
    }

    private void StopSimulation()
    {
        _frameTimer?.Stop();
        _frameTimer = null;
        if (_simulation is { } simulation)
        {
            _simulation = null;
            simulation.Cancel();
            simulation.Dispose();
        }
    }

    private void Apply(Action<ForceLayout> action)
    {
        lock (_gate)
        {
            if (Layout is not { } layout)
            {
                return;
            }

            if (_simulation is not null)
            {
                _pending.Enqueue(action);
                return;
            }

            action(layout);
            Capture(layout);
        }

        InvalidateVisual();
    }

    private void Drain(ForceLayout layout)
    {
        while (_pending.TryDequeue(out var action))
        {
            action(layout);
        }
    }

    private void Capture(ForceLayout layout)
    {
        if (_positions.Length != layout.Count)
        {
            _positions = new (double X, double Y)[layout.Count];
        }

        for (var i = 0; i < layout.Count; i++)
        {
            _positions[i] = layout.PositionOf(i);
        }
    }

    private (double X, double Y)[] Snapshot()
    {
        lock (_gate)
        {
            if (_read.Length != _positions.Length)
            {
                _read = new (double X, double Y)[_positions.Length];
            }

            Array.Copy(_positions, _read, _positions.Length);
        }

        return _read;
    }

    private void SetHovered(int node)
    {
        if (_hovered == node)
        {
            return;
        }

        _hovered = node;
        _neighbours.Clear();
        if (node >= 0 && Graph is { } graph)
        {
            foreach (var edge in graph.Edges)
            {
                if (edge.Source == node)
                {
                    _neighbours.Add(edge.Target);
                }
                else if (edge.Target == node)
                {
                    _neighbours.Add(edge.Source);
                }
            }
        }

        InvalidateVisual();
    }

    private void DrawEdges(DrawingContext context, GraphModel graph, (double X, double Y)[] positions)
    {
        var normal = Brush("BasaltBorderBrush") ?? Brushes.Gray;
        var lit = Brush("BasaltLinkBrush") ?? Brushes.DarkOrchid;
        foreach (var edge in graph.Edges)
        {
            if (edge.Source >= positions.Length || edge.Target >= positions.Length)
            {
                continue;
            }

            var incident = _hovered < 0 || edge.Source == _hovered || edge.Target == _hovered;
            var brush = _hovered < 0 ? normal : incident ? lit : Fade(normal, 0.2);
            var pen = new Pen(brush, incident && _hovered >= 0 ? 2 : 1);
            context.DrawLine(pen,
                ToScreen(positions[edge.Source].X, positions[edge.Source].Y),
                ToScreen(positions[edge.Target].X, positions[edge.Target].Y));
        }
    }

    private void DrawNodes(DrawingContext context, GraphModel graph, (double X, double Y)[] positions)
    {
        var accent = Brush("BasaltAccentBrush") ?? Brushes.MediumPurple;
        var label = new Typeface(FontFamily.Default);
        var count = Math.Min(graph.Nodes.Count, positions.Length);
        for (var i = 0; i < count; i++)
        {
            var node = graph.Nodes[i];
            var centre = ToScreen(positions[i].X, positions[i].Y);
            var lit = IsNodeLit(i);
            var fill = Fade(NodeBrush(node, accent), lit ? 1 : 0.25);
            context.DrawEllipse(fill, null, centre, NodeRadius, NodeRadius);
            if (i == _hovered)
            {
                context.DrawEllipse(null, new Pen(accent, 2), centre, NodeRadius + 3, NodeRadius + 3);
            }

            if (AreLabelsVisible)
            {
                var text = new FormattedText(
                    node.Label,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    label,
                    11,
                    Fade(Brush("BasaltTextSecondaryBrush") ?? Brushes.DimGray, lit ? 1 : 0.35));
                context.DrawText(text, new Point(centre.X + NodeRadius + 3, centre.Y - 7));
            }
        }
    }

    private static IBrush NodeBrush(GraphNode node, IBrush accent) => node.Kind switch
    {
        GraphNodeKind.Tag => Brush("BasaltTagBrush") ?? accent,
        GraphNodeKind.Unresolved => Brush("BasaltUnresolvedLinkBrush") ?? accent,
        GraphNodeKind.Attachment => Brush("BasaltTextMutedBrush") ?? accent,
        _ => node.Group >= 0 ? Brush(GroupBrushes[node.Group % GroupBrushes.Length]) ?? accent : accent,
    };

    private static IBrush Fade(IBrush brush, double opacity) =>
        opacity < 1 && brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, opacity) : brush;

    private static IBrush? Brush(string key) =>
        Avalonia.Application.Current?.TryFindResource(key, Avalonia.Application.Current.ActualThemeVariant, out var value) == true ? value as IBrush : null;
}
