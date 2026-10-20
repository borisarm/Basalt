using Basalt.Application.Abstractions;
using Basalt.Application.Graph;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.Platform;
using Basalt.Domain.Common;
using Basalt.Domain.Graph;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Workspace;

/// <summary>
/// The global graph tab (F-012, spec 07 §7): loads the whole graph of the vault, keeps its layout and exposes the
/// filters, colour groups and forces of the settings panel. It does not stand for a file (<see cref="Path"/> is null).
/// </summary>
public sealed partial class GraphTabViewModel : TabViewModel
{
    public const string KindId = "graph";

    public const double MinRepulsion = 10;
    public const double MaxRepulsion = 10000;
    public const double MinLinkDistance = 1;
    public const double MaxLinkDistance = 1000;
    public const double MinLinkStrength = 0;
    public const double MaxLinkStrength = 1;
    public const double MinGravity = 0;
    public const double MaxGravity = 1;

    private readonly VaultId _vault;
    private readonly IQueryHandler<GetGraph, GraphModel> _graph;
    private readonly IWorkspaceNavigator _navigator;
    private readonly IRandom _random;
    private readonly IDisposable _metadata;
    private Task _refresh = Task.CompletedTask;

    public GraphTabViewModel(
        ViewModelServices services,
        VaultId vault,
        IQueryHandler<GetGraph, GraphModel> graph,
        IWorkspaceNavigator navigator,
        IRandom random,
        IEventBus events)
        : base(services)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(events);
        _vault = vault;
        _graph = graph;
        _navigator = navigator;
        _random = random;
        Title = services.Localizer.Get("Graph.Title");
        _metadata = events.Subscribe<MetadataChanged>((e, _) =>
        {
            if (e.Id == vault)
            {
                Services.Dispatcher.Post(StartRefresh);
            }

            return ValueTask.CompletedTask;
        });
    }

    public override string Kind => KindId;

    [ObservableProperty]
    public partial GraphModel? Graph { get; private set; }

    [ObservableProperty]
    public partial ForceLayout? Layout { get; private set; }

    /// <summary>What the graph query returns: filters, search query and colour groups.</summary>
    [ObservableProperty]
    public partial GraphOptions Options { get; private set; } = GraphOptions.Default;

    [ObservableProperty]
    public partial ForceSettings Forces { get; private set; } = ForceSettings.Default;

    [ObservableProperty]
    public partial bool ShowOrphans { get; set; } = GraphOptions.Default.ShowOrphans;

    [ObservableProperty]
    public partial bool ShowAttachments { get; set; } = GraphOptions.Default.ShowAttachments;

    [ObservableProperty]
    public partial bool ShowUnresolved { get; set; } = GraphOptions.Default.ShowUnresolved;

    [ObservableProperty]
    public partial bool ShowTags { get; set; } = GraphOptions.Default.ShowTags;

    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    /// <summary>One search query per line; the first group that matches a note colours it.</summary>
    [ObservableProperty]
    public partial string ColorGroupsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Repulsion { get; set; } = ForceSettings.Default.Repulsion;

    [ObservableProperty]
    public partial double LinkDistance { get; set; } = ForceSettings.Default.LinkDistance;

    [ObservableProperty]
    public partial double LinkStrength { get; set; } = ForceSettings.Default.LinkStrength;

    [ObservableProperty]
    public partial double Gravity { get; set; } = ForceSettings.Default.Gravity;

    /// <summary>Completes when the refresh in progress (if any) has finished.</summary>
    public Task WhenRefreshedAsync() => _refresh;

    protected override Task<Result<Unit>> LoadAsync(OpenRequest request, CancellationToken ct) => ReloadAsync(ct);

    protected override Task<Result<Unit>> LoadAsync(CancellationToken ct) => ReloadAsync(ct);

    /// <summary>Opens the node's note; Ctrl/Cmd does it in a new tab.</summary>
    [RelayCommand]
    private Task OpenNodeAsync(GraphNode? node, CancellationToken ct) => OpenNodeAsync(node, newTab: false, ct);

    [RelayCommand]
    private Task OpenNodeNewTabAsync(GraphNode? node, CancellationToken ct) => OpenNodeAsync(node, newTab: true, ct);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _metadata.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task<Result<Unit>> ReloadAsync(CancellationToken ct)
    {
        var options = Options;
        var result = await RunAsync(token => _graph.HandleAsync(new GetGraph(_vault, options), token)).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return result.Error;
        }

        // The settings may have changed while the query ran: the stale answer would fight the newer one.
        if (options != Options)
        {
            return Result.Success();
        }

        Graph = result.Value;
        Layout = new ForceLayout(result.Value, Forces, _random.NextDouble);
        return Result.Success();
    }

    private void StartRefresh() => _refresh = ReloadAsync(CancellationToken.None);

    private Task OpenNodeAsync(GraphNode? node, bool newTab, CancellationToken ct) =>
        node?.Path is { } path ? _navigator.OpenAsync(new OpenRequest(path, NewTab: newTab), ct) : Task.CompletedTask;

    partial void OnShowOrphansChanged(bool value) => Options = Options with { ShowOrphans = value };

    partial void OnShowAttachmentsChanged(bool value) => Options = Options with { ShowAttachments = value };

    partial void OnShowUnresolvedChanged(bool value) => Options = Options with { ShowUnresolved = value };

    partial void OnShowTagsChanged(bool value) => Options = Options with { ShowTags = value };

    partial void OnFilterChanged(string value) =>
        Options = Options with { Filter = string.IsNullOrWhiteSpace(value) ? null : value.Trim() };

    partial void OnColorGroupsTextChanged(string value)
    {
        var groups = value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Options = Options with { ColorGroups = groups.Length == 0 ? null : groups };
    }

    partial void OnOptionsChanged(GraphOptions value) => StartRefresh();

    partial void OnRepulsionChanged(double value)
    {
        var clamped = Math.Clamp(value, MinRepulsion, MaxRepulsion);
        if (clamped != value)
        {
            Repulsion = clamped;
            return;
        }

        Forces = Forces with { Repulsion = clamped };
    }

    partial void OnLinkDistanceChanged(double value)
    {
        var clamped = Math.Clamp(value, MinLinkDistance, MaxLinkDistance);
        if (clamped != value)
        {
            LinkDistance = clamped;
            return;
        }

        Forces = Forces with { LinkDistance = clamped };
    }

    partial void OnLinkStrengthChanged(double value)
    {
        var clamped = Math.Clamp(value, MinLinkStrength, MaxLinkStrength);
        if (clamped != value)
        {
            LinkStrength = clamped;
            return;
        }

        Forces = Forces with { LinkStrength = clamped };
    }

    partial void OnGravityChanged(double value)
    {
        var clamped = Math.Clamp(value, MinGravity, MaxGravity);
        if (clamped != value)
        {
            Gravity = clamped;
            return;
        }

        Forces = Forces with { Gravity = clamped };
    }

    partial void OnForcesChanged(ForceSettings value)
    {
        if (Graph is { } graph)
        {
            Layout = new ForceLayout(graph, value, _random.NextDouble);
        }
    }
}
