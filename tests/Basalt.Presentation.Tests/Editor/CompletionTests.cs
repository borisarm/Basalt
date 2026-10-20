using Basalt.Presentation.Editor;

namespace Basalt.Presentation.Tests.Editor;

public sealed class CompletionTests
{
    private static CompletionContext? Detect(string marked)
    {
        var caret = marked.IndexOf('‸', StringComparison.Ordinal);
        return CompletionTrigger.Detect(marked.Remove(caret, 1), caret);
    }

    [Theory]
    [Trait("Spec", "F-004.1")]
    [InlineData("ver [[pro‸", CompletionKind.Link, "pro", 6)]
    [InlineData("ver [[pro‸]]", CompletionKind.Link, "pro", 6)]
    [InlineData("[[‸", CompletionKind.Link, "", 2)]
    [InlineData("![[img‸", CompletionKind.Link, "img", 3)]
    [InlineData("[[Proyecto X#‸", CompletionKind.Link, "Proyecto X#", 2)]
    [InlineData("[[Proyecto X#^blo‸", CompletionKind.Link, "Proyecto X#^blo", 2)]
    [InlineData("a #tra‸", CompletionKind.Tag, "tra", 3)]
    [InlineData("#tra‸", CompletionKind.Tag, "tra", 1)]
    [InlineData("x #trabajo/cli‸", CompletionKind.Tag, "trabajo/cli", 3)]
    [InlineData("(#a‸", CompletionKind.Tag, "a", 2)]
    public void Detects(string text, CompletionKind kind, string partial, int start)
    {
        Detect(text).Should().Be(new CompletionContext(kind, partial, start));
    }

    [Theory]
    [InlineData("[[a]] b‸")]
    [InlineData("[[a\nb‸")]
    [InlineData("texto‸")]
    [InlineData("C#‸")]
    [InlineData("a#b‸")]
    [InlineData("# ‸")]
    [InlineData("#‸")]
    [InlineData("‸")]
    [InlineData("[[a|alias‸")]
    public void NoContext(string text)
    {
        Detect(text).Should().BeNull();
    }
}
