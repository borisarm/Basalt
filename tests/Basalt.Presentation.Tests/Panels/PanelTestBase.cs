using Basalt.Application.Events;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Panels;
using Basalt.Presentation.Workspace;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.Presentation.Tests.Panels;

/// <summary>Shared wiring for panel ViewModel tests.</summary>
public abstract class PanelTestBase : IAsyncDisposable
{
    protected static readonly VaultId Vault = new(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));

    protected FakeTimeProvider Time { get; } = new();

    protected FakeNotificationService Notifications { get; } = new();

    protected FakeDialogService Dialogs { get; } = new();

    protected InMemoryEventBus Events { get; } = new(NullLogger<InMemoryEventBus>.Instance);

    protected WorkspaceMessenger Messenger { get; } = new();

    protected IWorkspaceNavigator Navigator { get; } = Substitute.For<IWorkspaceNavigator>();

    protected ViewModelServices Services => new(new ImmediateDispatcher(), Notifications, PresentationFakes.Localizer(), Time);

    protected PanelContext Context => new(Vault, Events, Messenger, Navigator);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static VaultPath P(string path) => VaultPath.Create(path).Value;

    protected void Activate(string? path) => Messenger.Send(new ActiveFileChanged(path is null ? null : P(path)));

    public async ValueTask DisposeAsync()
    {
        await Events.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
