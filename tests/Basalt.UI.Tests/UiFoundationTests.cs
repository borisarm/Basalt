using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Basalt.Application.Abstractions;
using Basalt.Application.Settings;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
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

public sealed class UiFoundationTests
{
    private static readonly string[] BrushTokens =
    [
        "BasaltBackgroundBrush", "BasaltSurfaceBrush", "BasaltSurfaceAltBrush", "BasaltBorderBrush", "BasaltTextPrimaryBrush",
        "BasaltTextSecondaryBrush", "BasaltTextMutedBrush", "BasaltAccentBrush", "BasaltOnAccentBrush", "BasaltDangerBrush", "BasaltWarningBrush",
        "BasaltSuccessBrush", "BasaltHighlightBrush", "BasaltLinkBrush", "BasaltUnresolvedLinkBrush", "BasaltTagBrush", "BasaltCodeBackgroundBrush",
    ];

    private static Color ColorOf(string token, ThemeVariant variant)
    {
        var app = Avalonia.Application.Current!;
        app.TryGetResource(token.Replace("Brush", "Color", StringComparison.Ordinal), variant, out var value).Should().BeTrue(token);
        return (Color)value!;
    }

    [Fact]
    [Trait("Spec", "UI-1")]
    public Task Tokens_ExistInLightAndDark_AndDiffer() => Headless.RunAsync(() =>
    {
        foreach (var token in BrushTokens)
        {
            Avalonia.Application.Current!.TryGetResource(token, ThemeVariant.Light, out var brush).Should().BeTrue(token);
            brush.Should().BeOfType<SolidColorBrush>();
            ColorOf(token, ThemeVariant.Light).Should().NotBe(ColorOf(token, ThemeVariant.Dark), token);
        }
    });

    [Fact]
    public Task Localization_FollowsTheCulture() => Headless.RunAsync(() =>
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("en"));
        LocalizationSource.Instance.Attach(localizer);
        var text = new TextBlock();
        text.Bind(TextBlock.TextProperty, (BindingBase)new LocExtension("Common.Cancel").ProvideValue(null!));
        var window = new Window { Content = text };
        window.Show();

        text.Text.Should().Be("Cancel");
        localizer.Culture = CultureInfo.GetCultureInfo("es");
        text.Text.Should().Be("Cancelar");
        LocalizationSource.Instance["Nope"].Should().Be("[Nope]");
        window.Close();
    });

    [Fact]
    public Task Dialogs_ReturnTheButtonChosen() => Headless.RunAsync(async () =>
    {
        var confirm = DialogWindows.Confirm(new ConfirmRequest("t", "¿Seguro?", "Borrar", IsDestructive: true));
        confirm.Window.Show();
        Click(confirm.Window, "Confirm");
        (await confirm.Result).Should().BeTrue();

        var cancelled = DialogWindows.Confirm(new ConfirmRequest("t", "m", "Sí"));
        cancelled.Window.Show();
        cancelled.Window.Close();
        (await cancelled.Result).Should().BeFalse();

        var prompt = DialogWindows.Prompt(new PromptRequest("t", "Nombre", "x"));
        prompt.Window.Show();
        Named<TextBox>(prompt.Window, "Input").Text = "Nuevo";
        Click(prompt.Window, "Confirm");
        (await prompt.Result).Should().Be("Nuevo");

        var choice = DialogWindows.Choose(new ChoiceRequest("t", "m", ["Uno", "Dos"]));
        choice.Window.Show();
        Click(choice.Window, "Option1");
        (await choice.Result).Should().Be(1);
    });

    [Fact]
    public Task Dispatcher_RunsOnTheUiThread() => Headless.RunAsync(async () =>
    {
        var dispatcher = new AvaloniaUiDispatcher();
        var ranOnUi = false;

        await Task.Run(() => dispatcher.InvokeAsync(() => ranOnUi = dispatcher.CheckAccess()));

        dispatcher.CheckAccess().Should().BeTrue();
        ranOnUi.Should().BeTrue();
    });

    [Fact]
    public Task Theme_IsApplied() => Headless.RunAsync(() =>
    {
        var app = Avalonia.Application.Current!;

        new ThemeService().Apply(ThemeMode.Dark, "#112233", 16);

        app.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);
        app.Resources["BasaltAccentColor"].Should().Be(Color.Parse("#112233"));
        new ThemeService().Apply(ThemeMode.System, null, 14);
        app.RequestedThemeVariant.Should().Be(ThemeVariant.Default);
    });

    [Fact]
    public Task MainWindow_HostsTheShell_AndShowsNotifications() => Headless.RunAsync(async () =>
    {
        var notifications = new NotificationService(new FakeTimeProvider());
        var services = new ViewModelServices(new AvaloniaUiDispatcher(), notifications, new ResxLocalizer(CultureInfo.GetCultureInfo("en")), new FakeTimeProvider());
        var list = Substitute.For<IQueryHandler<ListVaults, IReadOnlyList<VaultListItem>>>();
        list.HandleAsync(Arg.Any<ListVaults>(), Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<VaultListItem>>([]));
        var shell = new ShellViewModel(
            services,
            new ShellOptions(true),
            Substitute.For<ICommandHandler<OpenVault, VaultSessionInfo>>(),
            Substitute.For<ICommandHandler<CloseVault, Unit>>(),
            Substitute.For<IWindowService>(),
            opener => new VaultPickerViewModel(services, new VaultPickerUseCases(list, null!, null!, null!, null!), new FakeDialogService(), opener),
            _ => throw new InvalidOperationException());
        var window = new MainWindow(shell, notifications);
        window.Show();
        await shell.InitializeAsync(null, TestContext.Current.CancellationToken);

        notifications.Show(new Notification(NotificationSeverity.Error, "Algo falló"));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        window.Title.Should().Be("Basalt");
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain("Algo falló");
        window.Close();
    });

    // The dialogs are built in code, so their named controls are not in a name scope that FindControl could search.
    private static T Named<T>(Window window, string name)
        where T : Control => window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void Click(Window window, string name) =>
        ((Avalonia.Automation.Provider.IInvokeProvider)Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(Named<Button>(window, name))).Invoke();
}
