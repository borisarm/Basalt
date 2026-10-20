using System.Collections.ObjectModel;
using System.Globalization;
using Basalt.Application.Abstractions;
using Basalt.Application.Graph;
using Basalt.Application.Links;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Platform;
using Basalt.Domain.Common;
using Basalt.Domain.Graph;
using Basalt.Domain.Notes;
using Basalt.Presentation.Common;
using Basalt.Presentation.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Panels;

/// <summary>One linked or unlinked mention, with its line for context.</summary>
public sealed record MentionItemViewModel(VaultPath Source, int Line, string Context, TextRange Highlight, TextRange? Range = null);

/// <summary>Mentions grouped by the note they are in.</summary>
public sealed record MentionGroupViewModel(VaultPath Source, string Title, IReadOnlyList<MentionItemViewModel> Items)
{
    public int Count => Items.Count;
}

/// <summary>Backlinks of the active note (F-005): linked mentions and unlinked ones with "Link".</summary>
public sealed partial class BacklinksViewModel(
    ViewModelServices services,
    PanelContext context,
    IQueryHandler<GetBacklinks, Backlinks> backlinks,
    ICommandHandler<LinkUnlinkedMention, Unit> link) : ActiveNotePanelViewModel(services, context)
{
    public ObservableCollection<MentionGroupViewModel> Linked { get; } = [];

    public ObservableCollection<MentionGroupViewModel> Unlinked { get; } = [];

    protected override async Task RefreshAsync(VaultPath? file, CancellationToken ct)
    {
        if (file is not { } path)
        {
            Linked.Clear();
            Unlinked.Clear();
            return;
        }

        var result = await RunAsync(token => backlinks.HandleAsync(new GetBacklinks(Context.Vault, path), token)).ConfigureAwait(true);
        if (result.IsFailure || ActiveFile != path)
        {
            return;
        }

        Linked.Clear();
        foreach (var group in result.Value.Linked)
        {
            Linked.Add(new MentionGroupViewModel(group.Source, group.Source.NameWithoutExtension,
                [.. group.Links.Select(l => new MentionItemViewModel(group.Source, l.Context.Line, l.Context.Text, l.Context.Highlight))]));
        }

        Unlinked.Clear();
        foreach (var group in result.Value.Unlinked)
        {
            Unlinked.Add(new MentionGroupViewModel(group.Source, group.Source.NameWithoutExtension,
                [.. group.Mentions.Select(m => new MentionItemViewModel(group.Source, m.Context.Line, m.Context.Text, m.Context.Highlight, m.Range))]));
        }
    }

    [RelayCommand]
    private Task OpenAsync(MentionItemViewModel? item, CancellationToken ct) =>
        item is null ? Task.CompletedTask : Context.Navigator.OpenAsync(new OpenRequest(item.Source, Line: item.Line), ct);

    [RelayCommand]
    private async Task LinkMentionAsync(MentionItemViewModel? item)
    {
        if (item?.Range is not { } range || ActiveFile is not { } target)
        {
            return;
        }

        if ((await RunAsync(ct => link.HandleAsync(new LinkUnlinkedMention(Context.Vault, item.Source, range, target), ct)).ConfigureAwait(true)).IsSuccess)
        {
            StartRefresh();
        }
    }
}

/// <summary>An outgoing link of the active note.</summary>
public sealed record OutgoingLinkItemViewModel(string Target, bool IsResolved, VaultPath? Path);

/// <summary>Outgoing links of the active note, resolved and unresolved (F-005).</summary>
public sealed partial class OutgoingLinksViewModel(ViewModelServices services, PanelContext context, IQueryHandler<GetOutgoingLinks, IReadOnlyList<OutgoingLink>> outgoing)
    : ActiveNotePanelViewModel(services, context)
{
    public ObservableCollection<OutgoingLinkItemViewModel> Links { get; } = [];

    protected override async Task RefreshAsync(VaultPath? file, CancellationToken ct)
    {
        if (file is not { } path)
        {
            Links.Clear();
            return;
        }

        var result = await RunAsync(token => outgoing.HandleAsync(new GetOutgoingLinks(Context.Vault, path), token)).ConfigureAwait(true);
        if (result.IsFailure || ActiveFile != path)
        {
            return;
        }

        Links.Clear();
        foreach (var link in result.Value)
        {
            Links.Add(new OutgoingLinkItemViewModel(link.Link.RawTarget, link.Resolution.TargetPath is not null, link.Resolution.TargetPath));
        }
    }

    [RelayCommand]
    private Task OpenAsync(OutgoingLinkItemViewModel? link, CancellationToken ct) =>
        link?.Path is { } path ? Context.Navigator.OpenAsync(new OpenRequest(path), ct) : Task.CompletedTask;
}

/// <summary>A heading of the outline.</summary>
public sealed record OutlineItemViewModel(string Text, int Level, int Line, IReadOnlyList<OutlineItemViewModel> Children);

/// <summary>The outline of the active note (F-011); highlights the section of the caret.</summary>
public sealed partial class OutlineViewModel : ActiveNotePanelViewModel
{
    private readonly IQueryHandler<GetOutline, IReadOnlyList<OutlineNode>> _outline;
    private List<OutlineItemViewModel> _ordered = [];

    public OutlineViewModel(ViewModelServices services, PanelContext context, IQueryHandler<GetOutline, IReadOnlyList<OutlineNode>> outline)
        : base(services, context)
    {
        _outline = outline;
        context.Messenger.Register<CaretLineChanged>(this, (_, m) =>
        {
            if (m.Path == ActiveFile)
            {
                Current = _ordered.LastOrDefault(h => h.Line <= m.Line);
            }
        });
    }

    public ObservableCollection<OutlineItemViewModel> Roots { get; } = [];

    [ObservableProperty]
    public partial OutlineItemViewModel? Current { get; private set; }

    protected override async Task RefreshAsync(VaultPath? file, CancellationToken ct)
    {
        Current = null;
        if (file is not { } path)
        {
            Roots.Clear();
            _ordered = [];
            return;
        }

        var outline = await RunAsync(token => _outline.HandleAsync(new GetOutline(Context.Vault, path), token)).ConfigureAwait(true);
        if (outline.IsFailure || ActiveFile != path)
        {
            return;
        }

        Roots.Clear();
        foreach (var node in outline.Value)
        {
            Roots.Add(Convert(node));
        }

        _ordered = [.. Flatten(Roots).OrderBy(h => h.Line)];
    }

    private static OutlineItemViewModel Convert(OutlineNode node) =>
        new(node.Heading.Text, node.Heading.Level, node.Heading.Line, [.. node.Children.Select(Convert)]);

    private static IEnumerable<OutlineItemViewModel> Flatten(IEnumerable<OutlineItemViewModel> items) => items.SelectMany(i => Flatten(i.Children).Prepend(i));

    [RelayCommand]
    private Task OpenAsync(OutlineItemViewModel? heading, CancellationToken ct) =>
        heading is not null && ActiveFile is { } file ? Context.Navigator.OpenAsync(new OpenRequest(file, Line: heading.Line), ct) : Task.CompletedTask;
}

public sealed record PropertiesUseCases(
    IQueryHandler<ReadNote, NoteDocument> Read,
    ICommandHandler<SetFrontmatterProperty, Unit> Set,
    ICommandHandler<RemoveFrontmatterProperty, Unit> Remove);

/// <summary>A property of the frontmatter shown with typed editing (F-010).</summary>
public sealed record PropertyRow(string Key, ValueKind Kind, string Text);

/// <summary>The properties panel (F-010): typed rows over the frontmatter; invalid YAML is reported, not edited (F-010.2).</summary>
public sealed partial class PropertiesViewModel(ViewModelServices services, PanelContext context, PropertiesUseCases useCases) : ActiveNotePanelViewModel(services, context)
{
    public ObservableCollection<PropertyRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string? Warning { get; private set; }

    /// <summary>The text shown for a value: lists joined with commas, nested maps as <c>{…}</c>.</summary>
    public static string Render(StructuredValue value) => value switch
    {
        StructuredValue.TextValue text => text.Value,
        StructuredValue.NumberValue number => number.Raw,
        StructuredValue.BoolValue boolean => boolean.Value ? "true" : "false",
        StructuredValue.DateValue date => date.Raw,
        StructuredValue.DateTimeValue dateTime => dateTime.Raw,
        StructuredValue.ListValue list => string.Join(", ", list.Items.Select(Render)),
        StructuredValue.MapValue => "{…}",
        _ => string.Empty,
    };

    /// <summary>Parses the edited text keeping the row's type (numbers that do not parse stay text).</summary>
    public static StructuredValue Parse(PropertyRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var text = row.Text.Trim();
        return row.Kind switch
        {
            ValueKind.Number when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) => new StructuredValue.NumberValue(text),
            ValueKind.Bool when bool.TryParse(text, out var flag) => new StructuredValue.BoolValue(flag),
            ValueKind.List => new StructuredValue.ListValue([.. text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(t => (StructuredValue)new StructuredValue.TextValue(t))]),
            ValueKind.Date => new StructuredValue.DateValue(text),
            ValueKind.DateTime => new StructuredValue.DateTimeValue(text),
            _ when text.Length == 0 => StructuredValue.NullValue.Instance,
            _ => new StructuredValue.TextValue(text),
        };
    }

    protected override async Task RefreshAsync(VaultPath? file, CancellationToken ct)
    {
        Warning = null;
        if (file is not { } path)
        {
            Rows.Clear();
            return;
        }

        var note = await RunAsync(token => useCases.Read.HandleAsync(new ReadNote(Context.Vault, path), token)).ConfigureAwait(true);
        if (note.IsFailure || ActiveFile != path)
        {
            return;
        }

        Rows.Clear();
        if (note.Value.Metadata.Frontmatter is { IsValid: false })
        {
            Warning = Localizer.Get("Properties.Invalid");
            return;
        }

        foreach (var (key, value) in note.Value.Metadata.Frontmatter?.Values.Entries ?? [])
        {
            Rows.Add(new PropertyRow(key, value.Kind, Render(value)));
        }
    }

    [RelayCommand]
    private async Task CommitAsync(PropertyRow? row)
    {
        if (row is not null && ActiveFile is { } file
            && (await RunAsync(ct => useCases.Set.HandleAsync(new SetFrontmatterProperty(Context.Vault, file, row.Key, Parse(row)), ct)).ConfigureAwait(true)).IsSuccess)
        {
            StartRefresh();
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(PropertyRow? row)
    {
        if (row is not null && ActiveFile is { } file
            && (await RunAsync(ct => useCases.Remove.HandleAsync(new RemoveFrontmatterProperty(Context.Vault, file, row.Key), ct)).ConfigureAwait(true)).IsSuccess)
        {
            StartRefresh();
        }
    }
}

/// <summary>The local graph of the active note (F-012.1); the UI animates <see cref="Layout"/>.</summary>
public sealed partial class LocalGraphViewModel(ViewModelServices services, PanelContext context, IQueryHandler<GetLocalGraph, GraphModel> graph, IRandom random)
    : ActiveNotePanelViewModel(services, context)
{
    private int _depth = GetGraphHandler.MinDepth;

    /// <summary>Hops from the note, 1–5.</summary>
    public int Depth
    {
        get => _depth;
        set
        {
            if (SetProperty(ref _depth, Math.Clamp(value, GetGraphHandler.MinDepth, GetGraphHandler.MaxDepth)))
            {
                StartRefresh();
            }
        }
    }

    [ObservableProperty]
    public partial LocalGraphDirection Direction { get; set; }

    [ObservableProperty]
    public partial GraphOptions Options { get; set; } = GraphOptions.Default;

    [ObservableProperty]
    public partial GraphModel? Graph { get; private set; }

    [ObservableProperty]
    public partial ForceLayout? Layout { get; private set; }

    partial void OnDirectionChanged(LocalGraphDirection value) => StartRefresh();

    partial void OnOptionsChanged(GraphOptions value) => StartRefresh();

    protected override async Task RefreshAsync(VaultPath? file, CancellationToken ct)
    {
        if (file is not { } path)
        {
            Graph = null;
            Layout = null;
            return;
        }

        var model = await RunAsync(token => graph.HandleAsync(new GetLocalGraph(Context.Vault, path, Depth, Direction, Options), token)).ConfigureAwait(true);
        if (model.IsSuccess && ActiveFile == path)
        {
            Graph = model.Value;
            Layout = new ForceLayout(model.Value, ForceSettings.Default, random.NextDouble);
        }
    }

    [RelayCommand]
    private Task OpenNodeAsync(GraphNode? node, CancellationToken ct) =>
        node?.Path is { } path ? Context.Navigator.OpenAsync(new OpenRequest(path), ct) : Task.CompletedTask;
}
