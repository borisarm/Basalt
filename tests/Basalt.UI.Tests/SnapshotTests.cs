using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Basalt.Application.Commands;
using Basalt.Composition;
using Basalt.Domain.Notes;
using Basalt.Presentation.Common;
using Basalt.Presentation.Localization;
using Basalt.Presentation.Shell;
using Basalt.Presentation.Workspace;
using Basalt.Testing;
using Basalt.Testing.Fakes;
using Basalt.UI.Localization;
using Basalt.UI.Services;
using Basalt.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Basalt.UI.Tests;

/// <summary>Reference captures of the key views in both themes (spec 09 §5.5).</summary>
public sealed class SnapshotTests
{
    private static readonly Dictionary<string, string> Files = new()
    {
        ["Bienvenida.md"] = "# Bienvenida\n\nEsta nota muestra el **editor**.\n\n- [ ] una tarea\n- [x] otra tarea\n\nEnlaza a [[Segunda]].\n\n> Una cita\n\n```csharp\nvar x = 1;\n```\n",
        ["Segunda.md"] = "# Segunda\n\nTexto de la segunda nota.\n",
        ["Carpeta/Tercera.md"] = "# Tercera\n\nDentro de una carpeta.\n",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static ThemeVariant Theme(string name) => name == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        // Render frames deterministically so time-based transitions and asynchronous layout have finished.
        for (var i = 0; i < 5; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static async Task CaptureAsync(Window window, string name, ThemeVariant theme)
    {
        LocalizationSource.Instance.Attach(new ResxLocalizer(CultureInfo.GetCultureInfo("en")));
        Avalonia.Application.Current!.RequestedThemeVariant = theme;
        window.Width = 1200;
        window.Height = 800;
        if (!window.IsVisible)
        {
            window.Show();
        }

        for (var attempt = 0; ; attempt++)
        {
            // Views may focus their input from a posted job, so settle first and only then drop focus: a focused text
            // box draws a blinking caret and an accent border that differ from run to run.
            await SettleAsync();
            window.FocusManager?.Focus(null);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame()!;
            try
            {
                PngSnapshot.Match(frame, $"{name}.{theme}");
                return;
            }
            catch (SnapshotMismatchException) when (attempt < 2)
            {
                // Transitions and asynchronous layout can still be mid-flight on a busy runner; settle and recapture.
            }
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [Trait("Spec", "UI-7")]
    public Task Settings(string theme) => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(Files);
        var window = new SettingsWindow(v.Services.GetRequiredService<ShellFactory>().CreateSettings(v.Vault));
        window.Show();
        await SettleAsync();
        v.Services.GetRequiredService<ILocalizer>().Culture = CultureInfo.GetCultureInfo("en");
        await CaptureAsync(window, "Settings", Theme(theme));
        v.Services.GetRequiredService<ILocalizer>().Culture.Name.Should().Be("en");
        LocalizationSource.Instance["Settings.Editor"].Should().Be("Editor");
        window.Close();
    });

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [Trait("Spec", "UI-7")]
    public Task Workspace_Reading(string theme) => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(Files);
        await v.Workspace.OpenAsync(new OpenRequest(P("Bienvenida.md")), Ct);
        var note = v.Workspace.Layout.ActiveGroup.ActiveTab.Should().BeOfType<NoteTabViewModel>().Subject;
        await note.ToggleReadingModeCommand.ExecuteAsync(null);
        await SettleAsync();
        note.Rendered.Should().NotBeNull();
        await CaptureAsync(v.Window, "Workspace_Reading", Theme(theme));
    });

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [Trait("Spec", "UI-7")]
    public Task QuickSwitcher(string theme) => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(Files);
        await v.Workspace.OpenAsync(new OpenRequest(P("Bienvenida.md")), Ct);
        var switcher = v.Workspace.Panels!.QuickSwitcher;
        await switcher.ShowAsync();
        await switcher.WhenUpdatedAsync();
        await CaptureAsync(v.Window, "QuickSwitcher", Theme(theme));
    });

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [Trait("Spec", "UI-7")]
    public Task CommandPalette(string theme) => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(Files);
        v.Workspace.Panels!.CommandPalette.Show();
        await CaptureAsync(v.Window, "CommandPalette", Theme(theme));
    });

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [Trait("Spec", "UI-7")]
    public Task VaultPicker(string theme) => Headless.RunAsync(async () =>
    {
        var root = TempDirectory.Create("ui-snapshot-picker");
        var services = new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddBasaltCore(Path.Combine(root.Path, "appdata"))
            .AddBasaltPresentation(CommandPlatform.Windows, new ShellOptions(MultipleWindows: true));
        services.AddSingleton<IDialogService, FakeDialogService>();
        services.AddSingleton(Substitute.For<IWindowService>());
        services.AddBasaltUi();
        await using var provider = services.BuildServiceProvider();
        await using var temp = root;
        var shell = provider.GetRequiredService<ShellFactory>().Create();
        var window = new MainWindow(shell, provider.GetRequiredService<NotificationService>());
        window.Show();
        await shell.InitializeAsync(null, Ct);
        await CaptureAsync(window, "VaultPicker", Theme(theme));
        window.Close();
    });
}
