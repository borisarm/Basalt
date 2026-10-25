using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Basalt.Presentation.Localization;
using Basalt.UI.Views;

namespace Basalt.UI.Tests;

/// <summary>Spec 07 §1 and NFR-040…044: keyboard access, screen-reader names, contrast and touch targets.</summary>
public sealed partial class AccessibilityTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Basalt.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static IEnumerable<string> AxamlFiles() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "src", "Basalt.UI"), "*.axaml", SearchOption.AllDirectories);

    [Fact]
    [Trait("Spec", "NFR-040")]
    public Task Ribbon_IsReachableWithTheKeyboard() => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(new Dictionary<string, string>());
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        string[] ribbon =
        [
            "ToggleLeft", "QuickSwitcherButton", "PaletteButton", "GraphButton",
            "ConflictsButton", "HistoryButton", "SettingsButton", "ToggleRight",
        ];
        var buttons = v.Window.GetVisualDescendants().OfType<Button>().Where(b => ribbon.Contains(b.Name)).ToList();
        buttons.Should().HaveCount(ribbon.Length);
        foreach (var button in buttons)
        {
            button.Focusable.Should().BeTrue($"{button.Name} is a keyboard target");
            AutomationProperties.GetName(button).Should().NotBeNullOrWhiteSpace($"{button.Name} has a screen-reader name");
            button.Focus();
            button.IsFocused.Should().BeTrue($"{button.Name} accepts keyboard focus");
        }
    });

    [Fact]
    [Trait("Spec", "NFR-041")]
    public void InteractiveControls_HaveAnAccessibleName()
    {
        var stateless = new HashSet<string>(StringComparer.Ordinal)
        {
            "TextBox", "ComboBox", "ListBox", "TreeView", "Slider", "AutoCompleteBox",
        };
        var withContent = new HashSet<string>(StringComparer.Ordinal) { "Button", "CheckBox", "ComboBoxItem", "MenuItem" };
        var failures = new List<string>();
        foreach (var file in AxamlFiles())
        {
            var document = XDocument.Load(file);
            foreach (var element in document.Descendants())
            {
                var name = element.Name.LocalName;
                var accessible = element.Attribute("AutomationProperties.Name")?.Value;
                var content = element.Attribute("Content")?.Value
                    ?? element.Attribute("Header")?.Value
                    ?? element.Attribute("Text")?.Value;
                if (stateless.Contains(name) && string.IsNullOrWhiteSpace(accessible))
                {
                    failures.Add($"{Path.GetFileName(file)} <{name}> needs AutomationProperties.Name");
                }
                else if (withContent.Contains(name)
                    && string.IsNullOrWhiteSpace(accessible)
                    && string.IsNullOrWhiteSpace(content))
                {
                    failures.Add($"{Path.GetFileName(file)} <{name}> needs a name or visible content");
                }
            }
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "NFR-042")]
    public void ThemeTokens_MeetWcagAaContrast()
    {
        var document = XDocument.Load(Path.Combine(Root(), "src", "Basalt.UI", "Themes", "Tokens.axaml"));
        var themes = document.Root!
            .Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute(Xaml + "Key") is not null)
            .ToDictionary(
                e => e.Attribute(Xaml + "Key")!.Value,
                e => e.Elements()
                    .Where(c => c.Name.LocalName == "Color")
                    .ToDictionary(c => c.Attribute(Xaml + "Key")!.Value, c => Color.Parse(c.Value)));

        themes.Keys.Should().Contain(["Light", "Dark"]);
        string[] foregrounds =
        [
            "BasaltTextPrimaryColor", "BasaltTextSecondaryColor", "BasaltTextMutedColor", "BasaltAccentColor",
            "BasaltDangerColor", "BasaltWarningColor", "BasaltSuccessColor", "BasaltLinkColor",
            "BasaltUnresolvedLinkColor", "BasaltTagColor",
        ];
        string[] backgrounds =
        [
            "BasaltBackgroundColor", "BasaltSurfaceColor", "BasaltSurfaceAltColor", "BasaltChromeColor", "BasaltSidebarColor",
        ];
        string[] textSurfaces =
        [
            "BasaltHighlightColor", "BasaltCodeBackgroundColor", "BasaltDiffAddedColor",
            "BasaltDiffRemovedColor", "BasaltDiffChangedColor", "BasaltCalloutColor", "BasaltAccentSoftColor",
        ];

        // Coloured text on its own soft surface: tag chips and the sync pill states.
        (string Foreground, string Background)[] tinted =
        [
            ("BasaltTagColor", "BasaltTagSoftColor"), ("BasaltAccentColor", "BasaltAccentSoftColor"),
            ("BasaltWarningColor", "BasaltWarningSoftColor"), ("BasaltDangerColor", "BasaltDangerSoftColor"),
        ];
        var failures = new List<string>();
        foreach (var (theme, colors) in themes)
        {
            foreach (var foreground in foregrounds)
            {
                foreach (var background in backgrounds)
                {
                    var ratio = Contrast(colors[foreground], colors[background]);
                    if (ratio < 4.5)
                    {
                        failures.Add($"{theme}: {foreground} on {background} = {ratio:F2}");
                    }
                }
            }

            foreach (var background in textSurfaces)
            {
                var ratio = Contrast(colors["BasaltTextPrimaryColor"], colors[background]);
                if (ratio < 4.5)
                {
                    failures.Add($"{theme}: BasaltTextPrimaryColor on {background} = {ratio:F2}");
                }
            }

            foreach (var background in new[] { "BasaltDiffAddedColor", "BasaltDiffRemovedColor", "BasaltDiffChangedColor" })
            {
                var ratio = Contrast(colors["BasaltTextMutedColor"], colors[background]);
                if (ratio < 4.5)
                {
                    failures.Add($"{theme}: BasaltTextMutedColor on {background} = {ratio:F2}");
                }
            }

            foreach (var (foreground, background) in tinted)
            {
                var ratio = Contrast(colors[foreground], colors[background]);
                if (ratio < 4.5)
                {
                    failures.Add($"{theme}: {foreground} on {background} = {ratio:F2}");
                }
            }

            var onAccent = Contrast(colors["BasaltOnAccentColor"], colors["BasaltAccentColor"]);
            if (onAccent < 4.5)
            {
                failures.Add($"{theme}: BasaltOnAccentColor on BasaltAccentColor = {onAccent:F2}");
            }
        }

        failures.Should().BeEmpty("normal text needs a WCAG 2.1 AA ratio of at least 4.5:1");
    }

    [Fact]
    [Trait("Spec", "NFR-043")]
    public Task MobileButtons_AreAtLeast44Points() => Headless.RunAsync(async () =>
    {
        await using var v = await ComposedVault.CreateAsync(new Dictionary<string, string>());
        Control[] views =
        [
            new MobileWorkspaceView { DataContext = v.Workspace },
            new MobileNoteView(),
            new MobileShellView(),
            new MobileSignInView(),
        ];
        foreach (var view in views)
        {
            var window = new Window { Width = 420, Height = 800, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var buttons = view.GetVisualDescendants().OfType<Button>().ToList();
            buttons.Should().NotBeEmpty($"{view.GetType().Name} has touch buttons");
            buttons.Should().AllSatisfy(button =>
            {
                button.MinWidth.Should().BeGreaterThanOrEqualTo(44, $"{view.GetType().Name}.{button.Name} width");
                button.MinHeight.Should().BeGreaterThanOrEqualTo(44, $"{view.GetType().Name}.{button.Name} height");
            });
            window.Close();
        }
    });

    [Fact]
    [Trait("Spec", "NFR-044")]
    public void Views_UseLocalizedTextOnly()
    {
        var keys = new ResxLocalizer(CultureInfo.GetCultureInfo("en"));
        foreach (var file in AxamlFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match match in LocRegex().Matches(text))
            {
                keys.Get(match.Groups["key"].Value).Should().NotStartWith("[", $"{Path.GetFileName(file)} references {match.Groups["key"].Value}");
            }

            var document = XDocument.Load(file);
            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    if (attribute.Name.Namespace != XNamespace.None
                        || attribute.Name.LocalName is not ("Text" or "Content" or "Header" or "PlaceholderText" or "Watermark" or "ToolTip.Tip" or "Title")
                        || attribute.Value.StartsWith('{')
                        || !attribute.Value.Any(char.IsLetter))
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"{Path.GetFileName(file)} has hard-coded visible text '{attribute.Value}' on <{element.Name.LocalName}>");
                }
            }
        }
    }

    private static double Contrast(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        var lighter = Math.Max(a, b);
        var darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Color color) =>
        0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);

    private static double Channel(byte value)
    {
        var channel = value / 255.0;
        return channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    [GeneratedRegex(@"\{l:Loc\s+(?<key>[^\s}]+)\s*\}")]
    private static partial Regex LocRegex();
}
