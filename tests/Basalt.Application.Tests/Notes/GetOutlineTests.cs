using Basalt.Application.Errors;
using Basalt.Application.Notes;
using Basalt.Application.Tests.TestSupport;
using Basalt.Domain.Vaults;
using static Basalt.Application.Tests.TestSupport.VaultFixture;

namespace Basalt.Application.Tests.Notes;

public sealed class GetOutlineTests
{
    [Fact]
    public async Task Outline_NestsHeadingsFromTheSavedFile()
    {
        await using var v = await VaultFixture.CreateAsync(new Dictionary<string, string>
        {
            ["n.md"] = "# One\n\n## Two\n\n### Three\n\n## Four\n\n# Five\n",
        });

        var outline = (await new GetOutlineHandler(v.Sessions, v.Analyzer).HandleAsync(new GetOutline(Id, P("n.md")), Ct)).Value;

        outline.Select(n => n.Heading.Text).Should().Equal("One", "Five");
        outline[0].Children.Select(n => n.Heading.Text).Should().Equal("Two", "Four");
        outline[0].Children[0].Children.Select(n => n.Heading.Text).Should().Equal("Three");
    }

    [Fact]
    public async Task Outline_ForAMissingNoteOrAClosedVault_IsAnError()
    {
        await using var v = await VaultFixture.CreateAsync();
        var closed = new VaultId(Guid.Parse("00000000-0000-0000-0000-0000000000ff"));
        var handler = new GetOutlineHandler(v.Sessions, v.Analyzer);

        (await handler.HandleAsync(new GetOutline(Id, P("none.md")), Ct)).Error.Code.Should().Be(ErrorCodes.EntryNotFound);
        (await handler.HandleAsync(new GetOutline(closed, P("n.md")), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
    }

    [Fact]
    public async Task Outline_RejectsNullQueries()
    {
        await using var v = await VaultFixture.CreateAsync();

        await FluentActions.Awaiting(() => new GetOutlineHandler(v.Sessions, v.Analyzer).HandleAsync(null!, Ct))
            .Should().ThrowAsync<ArgumentNullException>();
    }
}

