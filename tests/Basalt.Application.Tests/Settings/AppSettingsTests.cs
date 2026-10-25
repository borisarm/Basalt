using Basalt.Application.Ports.Storage;
using Basalt.Application.Settings;
using Basalt.Domain.Common;
using NSubstitute;

namespace Basalt.Application.Tests.Settings;

public sealed class AppSettingsTests
{
    [Fact]
    public void Default_HasTheDocumentedValues()
    {
        var settings = AppSettings.Default;
        var editor = settings.Editor;

        settings.Theme.Should().Be(ThemeMode.System);
        settings.Language.Should().BeNull();
        settings.AccentColor.Should().BeNull();
        settings.InterfaceFontSize.Should().Be(14);
        settings.OpenLastVault.Should().BeTrue();
        settings.LastVaultId.Should().BeNull();
        settings.Hotkeys.Should().BeEmpty();
        editor.FontFamily.Should().Be("Inter");
        editor.FontSize.Should().Be(15);
        editor.ReadableLineLength.Should().BeTrue();
        editor.ShowLineNumbers.Should().BeFalse();
        editor.WordWrap.Should().BeTrue();
        editor.SpellCheck.Should().BeTrue();
        editor.AutoPairBrackets.Should().BeTrue();
    }

    [Fact]
    public async Task Store_DelegatesLoadAndSave()
    {
        var settings = AppSettings.Default with { Theme = ThemeMode.Dark, Language = "es" };
        var store = Substitute.For<IDocumentStore<AppSettings>>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new DocumentLoad<AppSettings>(settings, DocumentLoadStatus.Loaded));
        store.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var wrapper = new AppSettingsStore(store);

        var loaded = await wrapper.GetAsync(CancellationToken.None);
        var saved = await wrapper.SaveAsync(settings, CancellationToken.None);

        loaded.Should().Be(settings);
        saved.IsSuccess.Should().BeTrue();
        await store.Received(1).SaveAsync(settings, Arg.Any<CancellationToken>());
    }
}
