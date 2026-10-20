using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Basalt.Application.Abstractions;
using Basalt.Application.Vaults;
using Basalt.Application.Workspace;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Localization;
using Basalt.Presentation.Shell;
using Basalt.Presentation.Vaults;
using Basalt.Testing.Fakes;
using Basalt.UI.Localization;
using Basalt.UI.Services;
using Basalt.UI.Views;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.UI.Tests;

public sealed class ShellViewsTests
{
    private static readonly VaultId Id = new(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));

    private static (MainWindow Window, ShellViewModel Shell, FakeDialogService Dialogs, ICommandHandler<OpenVault, VaultSessionInfo> Open) Build(bool withVaults)
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("en"));
        LocalizationSource.Instance.Attach(localizer);
        var notifications = new NotificationService(new FakeTimeProvider());
        var services = new ViewModelServices(new AvaloniaUiDispatcher(), notifications, localizer, new FakeTimeProvider());
        var list = Substitute.For<IQueryHandler<ListVaults, IReadOnlyList<VaultListItem>>>();
        list.HandleAsync(Arg.Any<ListVaults>(), Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<VaultListItem>>(withVaults
            ? [new VaultListItem(new VaultRegistration(Id, "Notas", "C:/v/Notas", default), false)]
            : []));
        var open = Substitute.For<ICommandHandler<OpenVault, VaultSessionInfo>>();
        open.HandleAsync(Arg.Any<OpenVault>(), Arg.Any<CancellationToken>()).Returns(new VaultSessionInfo(Id, "Notas", "C:/v/Notas", VaultSettings.Default, null));
        var dialogs = new FakeDialogService();
        var useCases = new Presentation.Workspace.WorkspaceUseCases(
            Substitute.For<ICommandHandler<RecordRecentFile, Unit>>(),
            Substitute.For<IQueryHandler<LoadWorkspace, WorkspaceState?>>(),
            Substitute.For<ICommandHandler<SaveWorkspace, Unit>>());
        var shell = new ShellViewModel(
            services,
            new ShellOptions(true),
            open,
            Substitute.For<ICommandHandler<CloseVault, Unit>>(),
            Substitute.For<IWindowService>(),
            opener => new VaultPickerViewModel(services, new VaultPickerUseCases(list, null!, null!, null!, null!), dialogs, opener),
            info => new Presentation.Workspace.WorkspaceViewModel(services, info, [], new WorkspaceMessenger(), useCases, Substitute.For<IWindowService>()));
        return (new MainWindow(shell, notifications), shell, dialogs, open);
    }

    private static void Click(Button button) => ((Avalonia.Automation.Provider.IInvokeProvider)Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(button)).Invoke();

    private static T Find<T>(Control root, string name)
        where T : Control => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [Fact]
    [Trait("Spec", "F-001")]
    public Task Picker_ListsVaults_AndOpensOne() => Headless.RunAsync(async () =>
    {
        var (window, shell, _, open) = Build(withVaults: true);
        window.Show();
        await shell.InitializeAsync(null, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        texts.Should().Contain(["Vaults", "Notas", "C:/v/Notas"]);
        Find<TextBlock>(window, "EmptyText").IsVisible.Should().BeFalse();

        var openButton = window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Open"));
        Click(openButton);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        await open.Received(1).HandleAsync(new OpenVault(Id), Arg.Any<CancellationToken>());
        window.GetVisualDescendants().OfType<WorkspaceView>().Should().ContainSingle();
        window.Title.Should().Be("Notas — Basalt");
        window.Close();
    });

    [Fact]
    public Task Picker_EmptyState_AndCreateButton() => Headless.RunAsync(async () =>
    {
        var (window, shell, dialogs, _) = Build(withVaults: false);
        window.Show();
        await shell.InitializeAsync(null, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        Find<TextBlock>(window, "EmptyText").IsVisible.Should().BeTrue();
        Click(Find<Button>(window, "CreateButton"));
        Dispatcher.UIThread.RunJobs();

        dialogs.Requests.OfType<PromptRequest>().Should().ContainSingle().Which.Label.Should().Be("Vault name");
        window.Close();
    });

    [Fact]
    public Task Window_HasTheNativeMenu() => Headless.RunAsync(() =>
    {
        var (window, _, _, _) = Build(withVaults: false);

        NativeMenu.GetMenu(window)!.Items.Should().ContainSingle();
    });
}
