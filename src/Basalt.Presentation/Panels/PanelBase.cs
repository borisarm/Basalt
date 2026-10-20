using Basalt.Application.Notes;
using Basalt.Application.Ports.Events;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Workspace;

namespace Basalt.Presentation.Panels;

/// <summary>Ids for <see cref="IWorkspaceNavigator.FocusPanel"/>.</summary>
public static class PanelIds
{
    public const string Files = "files";
    public const string Search = "search";
    public const string Bookmarks = "bookmarks";
    public const string Tags = "tags";
    public const string Backlinks = "backlinks";
    public const string Outgoing = "outgoing";
    public const string Outline = "outline";
    public const string LocalGraph = "local-graph";
    public const string Properties = "properties";
}

/// <summary>What every panel of a workspace shares.</summary>
public sealed record PanelContext(VaultId Vault, IEventBus Events, IWorkspaceMessenger Messenger, IWorkspaceNavigator Navigator);

/// <summary>
/// A panel that shows something about the active note (backlinks, outline, properties…): it refreshes when the active
/// file changes and when the vault's metadata changes.
/// </summary>
public abstract class ActiveNotePanelViewModel : ViewModelBase
{
    private readonly IDisposable _metadata;
    private Task _refresh = Task.CompletedTask;

    protected ActiveNotePanelViewModel(ViewModelServices services, PanelContext context)
        : base(services)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        context.Messenger.Register<ActiveFileChanged>(this, (_, m) =>
        {
            ActiveFile = m.Path;
            StartRefresh();
        });
        _metadata = context.Events.Subscribe<MetadataChanged>((e, _) =>
        {
            if (e.Id == context.Vault && ActiveFile is not null)
            {
                Services.Dispatcher.Post(StartRefresh);
            }

            return ValueTask.CompletedTask;
        });
    }

    protected PanelContext Context { get; }

    protected VaultPath? ActiveFile { get; private set; }

    /// <summary>Completes when the refresh in progress (if any) has finished.</summary>
    public Task WhenRefreshedAsync() => _refresh;

    protected void StartRefresh() => _refresh = RefreshAsync(ActiveFile, CancellationToken.None);

    /// <summary>Reloads for <paramref name="file"/>; null clears the panel.</summary>
    protected abstract Task RefreshAsync(VaultPath? file, CancellationToken ct);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _metadata.Dispose();
            Context.Messenger.UnregisterAll(this);
        }

        base.Dispose(disposing);
    }
}
