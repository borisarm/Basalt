using System.Globalization;
using Basalt.Domain.Common;
using Basalt.Domain.Templates;

namespace Basalt.Domain.Tests.Templates;

public sealed class TemplateRendererTests
{
    private static readonly TemplateContext Context = new(
        "Reunión",
        new DateTime(2026, 10, 19, 15, 42, 0, DateTimeKind.Unspecified),
        CultureInfo.GetCultureInfo("es-ES"));

    [Theory]
    [InlineData("# {{title}}", "# Reunión")]
    [InlineData("{{date}}", "2026-10-19")]
    [InlineData("{{time}}", "15:42")]
    [InlineData("# {{date:dddd D MMMM YYYY}}", "# lunes 19 octubre 2026")]
    [InlineData("{{time:HH[h]mm}}", "15h42")]
    [InlineData("{{ title }} / {{TITLE}}", "Reunión / Reunión")]
    [InlineData("{{date:YYYY}}-{{date:MM}}", "2026-10")]
    [InlineData("{{unknown}} {{title", "{{unknown}} {{title")]
    [InlineData("sin variables", "sin variables")]
    [InlineData("{{}}", "{{}}")]
    [Trait("Spec", "F-009.3")]
    public void Render_ReplacesVariables(string template, string expected)
    {
        TemplateRenderer.Render(template, Context).Should().Be(expected);
    }

    [Fact]
    public void Render_RejectsNulls()
    {
        FluentActions.Invoking(() => TemplateRenderer.Render(null!, Context)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => TemplateRenderer.Render("x", null!)).Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("Diario", "YYYY-MM-DD", "Diario/2026-10-19.md")]
    [InlineData("", "YYYY-MM-DD", "2026-10-19.md")]
    [InlineData("Diario", "YYYY/YYYY-MM-DD", "Diario/2026/2026-10-19.md")]
    [InlineData("Notas/Diario", "YYYY/MM/DD", "Notas/Diario/2026/10/19.md")]
    [InlineData("Diario", "dddd D MMMM YYYY", "Diario/lunes 19 octubre 2026.md")]
    [Trait("Spec", "F-009.1")]
    public void DailyNotePolicy_ComputesPath(string folder, string format, string expected)
    {
        DailyNotePolicy.PathFor(new DateOnly(2026, 10, 19), folder, format, CultureInfo.GetCultureInfo("es-ES")).Value.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("Diario", "YYYY:MM:DD")]
    [InlineData("../fuera", "YYYY")]
    [InlineData("Diario", "")]
    public void DailyNotePolicy_InvalidResult_IsError(string folder, string format)
    {
        var result = DailyNotePolicy.PathFor(new DateOnly(2026, 10, 19), folder, format, CultureInfo.InvariantCulture);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void DailyNotePolicy_EmptyFormat_ReportsSpecificError()
    {
        DailyNotePolicy.PathFor(new DateOnly(2026, 10, 19), "x", "", CultureInfo.InvariantCulture)
            .Error.Code.Should().Be(DomainErrors.DailyNoteInvalidFormat);
        FluentActions.Invoking(() => DailyNotePolicy.PathFor(default, null!, "YYYY", CultureInfo.InvariantCulture)).Should().Throw<ArgumentNullException>();
    }
}
