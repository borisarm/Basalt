using System.Collections.ObjectModel;
using Basalt.Application.Abstractions;
using Basalt.Application.Sync;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Sync;

public sealed record ConflictCenterUseCases(
    IQueryHandler<ListConflicts, IReadOnlyList<SyncConflictEntry>> List,
    ICommandHandler<ResolveConflict, Unit> Resolve);

/// <summary>Conflict copies and marker files with the four resolutions (F-015.6).</summary>
public sealed partial class ConflictCenterViewModel(ViewModelServices services, ConflictCenterUseCases useCases)
    : ViewModelBase(services)
{
    private VaultId _vault;

    public ObservableCollection<SyncConflictEntry> Conflicts { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResolveCommand))]
    public partial SyncConflictEntry? Selected { get; set; }

    [ObservableProperty]
    public partial string MergedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ConflictResolutionKind Resolution { get; set; } = ConflictResolutionKind.KeepLocal;

    public bool IsEmpty => Conflicts.Count == 0;

    public void Attach(VaultId vault)
    {
        _vault = vault;
        _ = LoadAsync();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var conflicts = await RunAsync(ct => useCases.List.HandleAsync(new ListConflicts(_vault), ct))
            .ConfigureAwait(true);
        if (conflicts.IsFailure)
        {
            return;
        }

        Conflicts.Clear();
        foreach (var conflict in conflicts.Value)
        {
            Conflicts.Add(conflict);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand(CanExecute = nameof(CanResolve))]
    private async Task ResolveAsync()
    {
        if (Selected is not { } conflict)
        {
            return;
        }

        var text = Resolution == ConflictResolutionKind.Merged ? MergedText : null;
        var resolved = await RunAsync(ct => useCases.Resolve.HandleAsync(
            new ResolveConflict(_vault, conflict.Path, Resolution, text), ct)).ConfigureAwait(true);
        if (resolved.IsFailure)
        {
            return;
        }

        Conflicts.Remove(conflict);
        Selected = null;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool CanResolve() => Selected is not null;
}
