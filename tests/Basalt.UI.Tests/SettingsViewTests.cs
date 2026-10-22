using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Basalt.Application.Settings;
using Basalt.Application.Vaults;
using Basalt.Composition;
using Basalt.Presentation.Localization;
using Basalt.Presentation.Settings;
using Basalt.UI.Localization;
using Basalt.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Basalt.UI.Tests;

public sealed class SettingsViewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static T Find<T>(Control root, string name)
        where T : Control => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void Click(Button button) =>
        ((IInvokeProvider)ControlAutomationPeer.CreatePeerForElement(button)).Invoke();

    private static List<string?> Texts(Control root) => [.. root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)];

    private static async Task<(SettingsWindow Window, IAppSettingsStore Store)> OpenSettingsAsync(ComposedVault v)
    {
        var window = new SettingsWindow(v.Services.GetRequiredService<ShellFactory>().CreateSettings(v.Vault));
        window.Show();
        await SettleAsync();
        return (window, v.Services.GetRequiredService<IAppSettingsStore>());
    }

    [Fact]
    [Trait("Spec", "F-014")]
    public Task Settings_ShowsTheSections_AndSavesTheChanges() => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(new Dictionary<string, string>());
        var (window, store) = await OpenSettingsAsync(v);
        v.Services.GetRequiredService<ILocalizer>().Culture.Name.Should().Be("en");
        LocalizationSource.Instance["Settings.Hotkeys"].Should().Be("Hotkeys");
        var view = window.GetVisualDescendants().OfType<SettingsView>().Single();

        Texts(view).Should().Contain(["General", "Editor", "Files and links", "Appearance", "Hotkeys", "Daily notes", "Templates", "Accounts", "Sync", "About"]);

        Find<ComboBox>(view, "Language").SelectedIndex = 2;
        await SettleAsync();
        (await store.GetAsync(Ct)).Language.Should().Be("es");
        LocalizationSource.Instance["Settings.Hotkeys"].Should().Be("Atajos de teclado");

        var lineNumbers = Find<CheckBox>(view, "ShowLineNumbers");
        lineNumbers.IsChecked = true;
        lineNumbers.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();
        (await store.GetAsync(Ct)).Editor.ShowLineNumbers.Should().BeTrue();

        Find<ComboBox>(view, "ThemeBox").SelectedIndex = 2;
        await SettleAsync();
        (await store.GetAsync(Ct)).Theme.Should().Be(ThemeMode.Dark);

        var list = Find<ListBox>(view, "HotkeysList");
        var box = list.GetVisualDescendants().OfType<TextBox>().First();
        var row = (HotkeyRow)box.DataContext!;
        box.Text = "Ctrl+Alt+N";
        list.GetVisualDescendants().OfType<Button>().First(b => b.Content?.ToString() == "Assign")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();
        (await store.GetAsync(Ct)).Hotkeys[row.CommandId].Should().Be("Ctrl+Alt+N");

        window.Close();
    });

    [Fact]
    [Trait("Spec", "F-015")]
    public Task Settings_ShowAccountsAndSyncSections() => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(new Dictionary<string, string>());
        var (window, _) = await OpenSettingsAsync(v);
        var view = window.GetVisualDescendants().OfType<SettingsView>().Single();

        Texts(view).Should().Contain(["Accounts", "No GitHub account is signed in on this device.", "Sync", "This vault is not syncing yet."]);

        window.Close();
    });

    [Fact]
    [Trait("Spec", "F-016")]
    public Task Settings_OpenFromTheRibbon_AndFromTheShortcut() => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(new Dictionary<string, string>());

        Click(Find<Button>(v.Window, "SettingsButton"));
        await SettleAsync();
        v.Windows.Received(1).OpenSettings(Arg.Is<VaultSessionInfo>(info => info.Id == v.Vault.Id));

        v.Window.GetVisualDescendants().OfType<TreeView>().First().Focus();
        v.Window.KeyPress(Key.OemComma, RawInputModifiers.Control, PhysicalKey.None, null);
        await SettleAsync();
        v.Windows.Received(2).OpenSettings(Arg.Is<VaultSessionInfo>(info => info.Id == v.Vault.Id));
    });
}
