using System.Collections;
using System.Globalization;
using System.Reflection;
using Basalt.Application.Commands;
using Basalt.Application.Errors;
using Basalt.Domain.Common;
using Basalt.Presentation.Localization;

namespace Basalt.Presentation.Tests;

public sealed class LocalizationTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es");

    private static HashSet<string> KeysOf(CultureInfo culture)
    {
        var set = ResxLocalizer.Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!;
        return [.. set.Cast<DictionaryEntry>().Select(e => (string)e.Key)];
    }

    private static IEnumerable<string> ErrorCodesOf(Type catalog) =>
        catalog.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    [Trait("Spec", "UI-1")]
    public void EveryKey_ExistsInEnglishAndSpanish()
    {
        var english = KeysOf(English.Parent == CultureInfo.InvariantCulture ? CultureInfo.InvariantCulture : English);
        var spanish = KeysOf(Spanish);

        english.Should().NotBeEmpty();
        english.Except(spanish).Should().BeEmpty("every English key needs a Spanish translation");
        spanish.Except(english).Should().BeEmpty("Spanish must not have keys missing in English");
    }

    [Fact]
    [Trait("Spec", "CONV-3")]
    public void EveryErrorCode_HasATranslation()
    {
        var keys = KeysOf(CultureInfo.InvariantCulture);

        ErrorCodesOf(typeof(DomainErrors)).Concat(ErrorCodesOf(typeof(ErrorCodes)))
            .Select(code => "Errors." + code)
            .Where(key => !keys.Contains(key))
            .Should().BeEmpty();
    }

    [Fact]
    public void EveryCoreCommand_HasATitle()
    {
        var keys = KeysOf(CultureInfo.InvariantCulture);
        var commands = new CoreCommands(null!, null!, null!).GetCommands();

        commands.Select(c => c.TitleKey).Where(k => !keys.Contains(k)).Should().BeEmpty();
    }

    [Fact]
    public void Lookup_FollowsTheCulture_AndRaisesCultureChanged()
    {
        var localizer = new ResxLocalizer(English);
        var raised = 0;
        localizer.CultureChanged += (_, _) => raised++;

        localizer.Get("Common.Cancel").Should().Be("Cancel");
        localizer.Culture = Spanish;

        localizer.Get("Common.Cancel").Should().Be("Cancelar");
        raised.Should().Be(1);
        localizer.Culture = Spanish;
        raised.Should().Be(1);
    }

    [Fact]
    public void MissingKeys_AreVisible_NotFatal()
    {
        new ResxLocalizer(English).Get("Nope.Missing").Should().Be("[Nope.Missing]");
    }

    [Fact]
    public void Format_UsesTheCultureForArguments()
    {
        var localizer = new ResxLocalizer(Spanish);

        localizer.Format("Common.ItemsCount", 1234).Should().Be("1.234 elementos");
        new ResxLocalizer(English).Format("Common.ItemsCount", 1234).Should().Be("1,234 items");
    }

    [Fact]
    public void Errors_AreLocalized_WithTheirMetadata()
    {
        var error = new Error(ErrorCodes.PathAlreadyExists, "irrelevant").With("path", "Notas/A.md");

        new ResxLocalizer(English).Error(error).Should().Be("“Notas/A.md” already exists.");
        new ResxLocalizer(Spanish).Error(error).Should().Be("Ya existe «Notas/A.md».");
        new ResxLocalizer(English).Error(new Error("unknown.code", "Raw message")).Should().Be("Raw message");
    }
}
