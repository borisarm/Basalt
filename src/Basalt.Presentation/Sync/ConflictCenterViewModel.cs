using System.Collections.ObjectModel;
using Basalt.Application.Abstractions;
using Basalt.Application.Sync;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Sync;

public sealed record ConflictCenterUseCases(
    IQueryHandler<ListConflicts, IReadOnlyList<SyncConflictEntry>> List,
    ICommandHandler<ResolveConflict, Unit> Resolve);

/// <summary>Conflict copies and marker files with the four resolutions (F-015.6).</summary>
public sealed partial class ConflictCenterViewModel(ViewModelServices services, ConflictCenterUseCases useCases)
    : TabViewModel(services)
{
    public const string KindId = "conflicts";

    private VaultId _vault;

    public override string Kind => KindId;

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
        Title = Localizer.Get("Sync.ConflictsTitle");
        _ = ReloadAsync();
    }

    protected override async Task<Result<Unit>> LoadAsync(OpenRequest request, CancellationToken ct) =>
        await LoadAsync(ct).ConfigureAwait(true);

    protected override async Task<Result<Unit>> LoadAsync(CancellationToken ct)
    {
        var conflicts = await RunAsync(token => useCases.List.HandleAsync(new ListConflicts(_vault), token)).ConfigureAwait(true);
        if (conflicts.IsFailure)
        {
            return conflicts.Error;
        }

        Conflicts.Clear();
        foreach (var conflict in conflicts.Value)
        {
            Conflicts.Add(conflict);
        }

        OnPropertyChanged(nameof(IsEmpty));
        return Result.Success();
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
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
