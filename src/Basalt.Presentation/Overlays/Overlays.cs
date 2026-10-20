using System.Collections.ObjectModel;
using Basalt.Application.Abstractions;
using Basalt.Application.Commands;
using Basalt.Application.Notes;
using Basalt.Application.Search;
using Basalt.Domain.Notes;
using Basalt.Domain.Search;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Overlays;

/// <summary>How a quick switcher entry is accepted: Enter, Ctrl/Cmd+Enter, Shift+Enter (spec 04 F-008).</summary>
public enum QuickSwitchAccept
{
    Open,
    NewTab,
    Create,
}

/// <summary>The quick switcher overlay (F-008).</summary>
public sealed partial class QuickSwitcherViewModel(
    ViewModelServices services,
    VaultId vault,
    IQueryHandler<QuickSwitch, IReadOnlyList<QuickSwitchItem>> quickSwitch,
    ICommandHandler<CreateNote, VaultPath> createNote,
    IWorkspaceNavigator navigator) : ViewModelBase(services)
{
    private int _generation;
    private Task _updating = Task.CompletedTask;

    public ObservableCollection<QuickSwitchItem> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedIndex { get; private set; }

    /// <summary>Completes when the list for the current text is shown.</summary>
    public Task WhenUpdatedAsync() => _updating;

    public Task ShowAsync()
    {
        IsOpen = true;
        if (Text.Length > 0)
        {
            Text = string.Empty;
        }
        else
        {
            _updating = UpdateAsync(string.Empty);
        }

        return _updating;
    }

    /// <summary>Up/Down arrows, wrapping around.</summary>
    public void MoveSelection(int delta)
    {
        if (Items.Count > 0)
        {
            SelectedIndex = (((SelectedIndex + delta) % Items.Count) + Items.Count) % Items.Count;
        }
    }

    partial void OnTextChanged(string value) => _updating = UpdateAsync(value);

    private async Task UpdateAsync(string text)
    {
        var generation = ++_generation;
        var items = await RunAsync(ct => quickSwitch.HandleAsync(new QuickSwitch(vault, text), ct)).ConfigureAwait(true);
        if (items.IsFailure || generation != _generation)
        {
            return;
        }

        Items.Clear();
        foreach (var item in items.Value)
        {
            Items.Add(item);
        }

        SelectedIndex = 0;
    }

    [RelayCommand]
    private async Task AcceptAsync(QuickSwitchAccept mode, CancellationToken ct)
    {
        await _updating.ConfigureAwait(true);
        var selected = SelectedIndex < Items.Count ? Items[SelectedIndex] : null;
        if (mode == QuickSwitchAccept.Create || selected is { Kind: QuickSwitchKind.Create })
        {
            var title = Text.Trim();
            if (title.Length == 0)
            {
                return;
            }

            var created = await RunAsync(token => createNote.HandleAsync(new CreateNote(vault, Title: title), token)).ConfigureAwait(true);
            if (created.IsFailure)
            {
                return;
            }

            Close();
            await navigator.OpenAsync(new OpenRequest(created.Value), ct).ConfigureAwait(true);
            return;
        }

        if (selected?.Path is { } path)
        {
            Close();
            await navigator.OpenAsync(new OpenRequest(path, NewTab: mode == QuickSwitchAccept.NewTab), ct).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void Close() => IsOpen = false;
}

/// <summary>A command in the palette with its shortcut.</summary>
public sealed record CommandItem(string Id, string Title, string? Gesture, IReadOnlyList<int> Positions);

/// <summary>The command palette (F-008): every available command, fuzzy search, most recently used first.</summary>
public sealed partial class CommandPaletteViewModel(
    ViewModelServices services,
    ICommandRegistry registry,
    KeyBindingService bindings,
    Func<CommandContext> context) : ViewModelBase(services)
{
    private readonly List<string> _recent = [];

    public ObservableCollection<CommandItem> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedIndex { get; set; }

    public void Show()
    {
        IsOpen = true;
        Text = string.Empty;
        Update();
    }

    partial void OnTextChanged(string value) => Update();

    private void Update()
    {
        var current = context();
        var available = registry.Commands.Where(c => c.CanExecute(current))
            .Select(c => (Command: c, Title: Localizer.Get(c.TitleKey)))
            .ToList();
        IEnumerable<CommandItem> items;
        if (string.IsNullOrWhiteSpace(Text))
        {
            items = available
                .OrderBy(c => _recent.IndexOf(c.Command.Id) is var i and >= 0 ? i : int.MaxValue)
                .ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => Item(c.Command, c.Title, []));
        }
        else
        {
            items = FuzzyMatcher.Rank(Text, available, c => c.Title).Select(r => Item(r.Item.Command, r.Item.Title, r.Match.Positions));
        }

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        SelectedIndex = 0;
    }

    private CommandItem Item(CommandDescriptor command, string title, IReadOnlyList<int> positions) =>
        new(command.Id, title, bindings.GestureFor(command.Id)?.ToString(bindings.Platform), positions);

    [RelayCommand]
    private async Task ExecuteAsync(CancellationToken ct)
    {
        if (SelectedIndex >= Items.Count)
        {
            return;
        }

        var id = Items[SelectedIndex].Id;
        IsOpen = false;
        _recent.Remove(id);
        _recent.Insert(0, id);
        await RunAsync(token => registry.ExecuteAsync(id, context(), token)).ConfigureAwait(true);
    }

    [RelayCommand]
    private void Close() => IsOpen = false;
}
