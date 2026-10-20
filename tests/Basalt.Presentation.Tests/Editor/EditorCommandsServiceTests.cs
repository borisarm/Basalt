using Basalt.Presentation.Editor;

namespace Basalt.Presentation.Tests.Editor;

/// <summary>
/// Table tests of the editor behaviours (spec 07 §5.2). Buffers use <c>‸</c> for the caret and <c>«…»</c> for a
/// selection; an expected value of null means "not handled: the editor does its default".
/// </summary>
public sealed class EditorCommandsServiceTests
{
    private readonly EditorCommandsService _service = new();

    private static TextBufferState Parse(string marked)
    {
        var caret = marked.IndexOf('‸', StringComparison.Ordinal);
        if (caret >= 0)
        {
            return new TextBufferState(marked.Remove(caret, 1), caret, 0);
        }

        var start = marked.IndexOf('«', StringComparison.Ordinal);
        var end = marked.IndexOf('»', StringComparison.Ordinal);
        return new TextBufferState(marked.Remove(end, 1).Remove(start, 1), start, end - start - 1);
    }

    private static string Render(TextBufferState state) => state.SelectionLength == 0
        ? state.Text.Insert(state.SelectionStart, "‸")
        : state.Text.Insert(state.SelectionStart + state.SelectionLength, "»").Insert(state.SelectionStart, "«");

    private static string? Run(Func<TextBufferState, EditResult?> command, string before)
    {
        var state = Parse(before);
        return command(state) is { } result ? Render(state.Apply(result)) : null;
    }

    [Theory]
    [Trait("Spec", "F-003.4")]
    [InlineData("- [ ] tarea‸", "- [ ] tarea\n- [ ] ‸")]
    [InlineData("- [ ] ‸", "‸")]
    [InlineData("- [x] hecha‸", "- [x] hecha\n- [ ] ‸")]
    [InlineData("- [X] hecha‸", "- [X] hecha\n- [ ] ‸")]
    [InlineData("- item‸", "- item\n- ‸")]
    [InlineData("* item‸", "* item\n* ‸")]
    [InlineData("+ item‸", "+ item\n+ ‸")]
    [InlineData("- ‸", "‸")]
    [InlineData("1. uno‸", "1. uno\n2. ‸")]
    [InlineData("9. nueve‸", "9. nueve\n10. ‸")]
    [InlineData("3) tres‸", "3) tres\n4) ‸")]
    [InlineData("1. ‸", "‸")]
    [InlineData("> cita‸", "> cita\n> ‸")]
    [InlineData(">cita‸", ">cita\n>‸")]
    [InlineData("> ‸", "‸")]
    [InlineData("  - anidado‸", "  - anidado\n  - ‸")]
    [InlineData("\t- tab‸", "\t- tab\n\t- ‸")]
    [InlineData("    1. sangrado‸", "    1. sangrado\n    2. ‸")]
    [InlineData("  - [ ] sub‸", "  - [ ] sub\n  - [ ] ‸")]
    [InlineData("- ab‸cd", "- ab\n- ‸cd")]
    [InlineData("- [ ] ab‸cd", "- [ ] ab\n- [ ] ‸cd")]
    [InlineData("a\n- item‸\nb", "a\n- item\n- ‸\nb")]
    [InlineData("- «sel»rest", "- \n- ‸rest")]
    [InlineData("texto‸", null)]
    [InlineData("‸", null)]
    [InlineData("-item‸", null)]
    [InlineData("‸- item", null)]
    [InlineData("  ‸- item", null)]
    [InlineData("```\n- item‸\n```", null)]
    [InlineData("1.uno‸", null)]
    public void Enter_ContinuesAndEndsLists(string before, string? after)
    {
        Run(_service.Enter, before).Should().Be(after);
    }

    [Theory]
    [InlineData("- item‸", "\t- item‸")]
    [InlineData("\t- item‸", "\t\t- item‸")]
    [InlineData("1. uno‸", "\t1. uno‸")]
    [InlineData("- [ ] t‸", "\t- [ ] t‸")]
    [InlineData("«- a\n- b»", "«\t- a\n\t- b»")]
    [InlineData("«- a\ntexto\n- b»", "«\t- a\ntexto\n\t- b»")]
    [InlineData("texto‸", null)]
    [InlineData("> cita‸", null)]
    public void Indent_ListItems(string before, string? after)
    {
        Run(_service.Indent, before).Should().Be(after);
    }

    [Theory]
    [InlineData("\t- item‸", "- item‸")]
    [InlineData("\t\t- item‸", "\t- item‸")]
    [InlineData("    - item‸", "- item‸")]
    [InlineData("  - item‸", "- item‸")]
    [InlineData("      - item‸", "  - item‸")]
    [InlineData("«\t- a\n\t- b»", "«- a\n- b»")]
    [InlineData("- item‸", null)]
    [InlineData("\ttexto‸", null)]
    public void Outdent_ListItems(string before, string? after)
    {
        Run(_service.Outdent, before).Should().Be(after);
    }

    [Theory]
    [InlineData("**", "a «b» c", "a **«b»** c")]
    [InlineData("**", "a **«b»** c", "a «b» c")]
    [InlineData("**", "a «**b**» c", "a «b» c")]
    [InlineData("**", "a ‸ c", "a **‸** c")]
    [InlineData("**", "a **‸** c", "a ‸ c")]
    [InlineData("*", "a «b» c", "a *«b»* c")]
    [InlineData("*", "a *«b»* c", "a «b» c")]
    [InlineData("*", "a **«b»** c", "a ***«b»*** c")]
    [InlineData("*", "a ***«b»*** c", "a **«b»** c")]
    [InlineData("**", "a ***«b»*** c", "a *«b»* c")]
    [InlineData("**", "a *«b»* c", "a ***«b»*** c")]
    [InlineData("`", "«code»", "`«code»`")]
    [InlineData("`", "`«code»`", "«code»")]
    [InlineData("~~", "«tachado»", "~~«tachado»~~")]
    [InlineData("~~", "~~«tachado»~~", "«tachado»")]
    [InlineData("==", "«resaltado»", "==«resaltado»==")]
    [InlineData("==", "==‸==", "‸")]
    [InlineData("`", "‸", "`‸`")]
    public void ToggleWrap_AddsOrRemovesMarkers(string marker, string before, string after)
    {
        Run(s => _service.ToggleWrap(s, marker), before).Should().Be(after);
    }

    [Theory]
    [InlineData("- [ ] t‸", "- [x] t‸")]
    [InlineData("- [x] t‸", "- [ ] t‸")]
    [InlineData("- [X] t‸", "- [ ] t‸")]
    [InlineData("- t‸", "- [ ] t‸")]
    [InlineData("t‸", "- [ ] t‸")]
    [InlineData("  - [ ] t‸", "  - [x] t‸")]
    [InlineData("1. t‸", "1. [ ] t‸")]
    [InlineData("‸", "- [ ] ‸")]
    [InlineData("a\n- [ ] ‸b\nc", "a\n- [x] ‸b\nc")]
    public void ToggleTask_OnTheCurrentLine(string before, string after)
    {
        Run(_service.ToggleTask, before).Should().Be(after);
    }

    [Theory]
    [InlineData("ver «https://basalt.dev»", "ver [‸](https://basalt.dev)")]
    [InlineData("«http://x.org/a?b=1»", "[‸](http://x.org/a?b=1)")]
    [InlineData("«www.example.com»", "[‸](www.example.com)")]
    [InlineData("ver «Proyecto X»", "ver [[Proyecto X‸]]")]
    [InlineData("‸", "[[‸]]")]
    [InlineData("a ‸b", "a [[‸]]b")]
    public void InsertLink_MarkdownForUrls_WikilinkOtherwise(string before, string after)
    {
        Run(_service.InsertLink, before).Should().Be(after);
    }

    [Theory]
    [InlineData('(', "a‸", "a(‸)")]
    [InlineData('(', "«x»", "(«x»)")]
    [InlineData(')', "(‸)", "()‸")]
    [InlineData(')', "‸", null)]
    [InlineData('`', "‸", "`‸`")]
    [InlineData('`', "`‸`", "``‸")]
    [InlineData('`', "«x»", "`«x»`")]
    [InlineData('[', "[‸", "[[‸]]")]
    [InlineData('[', "‸", null)]
    [InlineData('[', "[‸]", "[[‸]]")]
    [InlineData(']', "[[x‸]]", "[[x]‸]")]
    [InlineData('*', "*‸", "**‸**")]
    [InlineData('*', "‸", null)]
    [InlineData('*', "«b»", "*«b»*")]
    [InlineData('a', "‸", null)]
    [InlineData('(', "a‸b", null)]
    public void AutoClose_Pairs(char typed, string before, string? after)
    {
        Run(s => _service.AutoClose(s, typed), before).Should().Be(after);
    }

    [Fact]
    public void Apply_HandlesSeveralEdits_InAnyOrder()
    {
        var state = new TextBufferState("abc", 0, 0);

        var applied = state.Apply(new EditResult([new TextEdit(2, 1, "X"), new TextEdit(0, 0, "Y")], 4, 0));

        applied.Should().Be(new TextBufferState("YabX", 4, 0));
    }
}
