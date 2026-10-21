using System.Collections.ObjectModel;
using System.Text;
using Basalt.Application.Abstractions;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Sync;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Sync;

public sealed record FileHistoryUseCases(
    IQueryHandler<GetFileHistory, IReadOnlyList<RemoteCommitInfo>> History,
    IQueryHandler<GetFileVersion, byte[]?> Version,
    ICommandHandler<RestoreFileVersion, Unit> Restore);

/// <summary>Version history and restore of one file (F-015.7).</summary>
public sealed partial class FileHistoryViewModel(ViewModelServices services, FileHistoryUseCases useCases)
    : ViewModelBase(services)
{
    private VaultId _vault;

    public ObservableCollection<RemoteCommitInfo> Commits { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand), nameof(RestoreCommand))]
    public partial RemoteCommitInfo? Selected { get; set; }

    [ObservableProperty]
    public partial string? PreviewText { get; private set; }

    public VaultPath? Path { get; private set; }

    public bool IsEmpty => Commits.Count == 0;

    public void Attach(VaultId vault) => _vault = vault;

    [RelayCommand]
    public async Task OpenAsync(VaultPath path)
    {
        Path = path;
        var history = await RunAsync(ct => useCases.History.HandleAsync(new GetFileHistory(_vault, path, 100), ct))
            .ConfigureAwait(true);
        if (history.IsFailure)
        {
            return;
        }

        Commits.Clear();
        foreach (var commit in history.Value)
        {
            Commits.Add(commit);
        }

        Selected = Commits.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private async Task PreviewAsync()
    {
        if (Path is not { } path || Selected is not { } commit)
        {
            return;
        }

        var version = await RunAsync(ct => useCases.Version.HandleAsync(new GetFileVersion(_vault, path, commit.Sha), ct))
            .ConfigureAwait(true);
        PreviewText = version.IsFailure || version.Value is null
            ? null
            : Encoding.UTF8.GetString(version.Value);
    }

    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private async Task RestoreAsync()
    {
        if (Path is not { } path || Selected is not { } commit)
        {
            return;
        }

        var restored = await RunAsync(ct => useCases.Restore.HandleAsync(new RestoreFileVersion(_vault, path, commit.Sha), ct))
            .ConfigureAwait(true);
        if (restored.IsSuccess)
        {
            Services.Notifications.Show(new Notification(
                NotificationSeverity.Success, Localizer.Get("Sync.Restored")));
        }
    }

    private bool CanUseSelection() => Selected is not null;
}
