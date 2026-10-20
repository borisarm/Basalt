using Basalt.Application.Errors;
using Basalt.Application.Ports.Index;
using Basalt.Application.Search;
using Basalt.Application.Tests.TestSupport;
using Basalt.Domain.Common;
using Basalt.Domain.Search;
using Basalt.Domain.Vaults;
using static Basalt.Application.Tests.TestSupport.VaultFixture;

namespace Basalt.Application.Tests.Search;

public sealed class SearchNotesTests
{
    [Fact]
    [Trait("Spec", "F-007.1")]
    public async Task Search_ReturnsHitsFromTheVaultIndex()
    {
        await using var v = await VaultFixture.CreateAsync(new Dictionary<string, string>
        {
            ["a.md"] = "reunión con el cliente",
            ["b.md"] = "reunión interna",
        });

        var hits = await new SearchNotesHandler(v.Sessions).HandleAsync(new SearchNotes(Id, "reunión cliente"), Ct);

        hits.Value.Select(h => h.Path).Should().Equal(P("a.md"));
        hits.Value[0].Ranges.Should().HaveCount(2);
    }

    [Fact]
    [Trait("Spec", "F-007.3")]
    public async Task Search_Regex_HighlightsIsoDates()
    {
        await using var v = await VaultFixture.CreateAsync(new Dictionary<string, string> { ["a.md"] = "vence 2026-10-19", ["b.md"] = "sin fecha" });

        var hits = await new SearchNotesHandler(v.Sessions).HandleAsync(new SearchNotes(Id, @"/\d{4}-\d{2}-\d{2}/"), Ct);

        hits.Value.Should().ContainSingle().Which.Ranges.Should().Equal(new MatchRange(MatchTarget.Content, 6, 10));
    }

    [Fact]
    [Trait("Spec", "F-007.4")]
    public async Task InvalidQuery_IsAnErrorResult()
    {
        await using var v = await VaultFixture.CreateAsync();

        var result = await new SearchNotesHandler(v.Sessions).HandleAsync(new SearchNotes(Id, "/[a-/"), Ct);

        result.Error.Code.Should().Be(DomainErrors.SearchInvalidRegex);
    }

    [Fact]
    public async Task Search_PassesOptions_AndRequiresAnOpenVault()
    {
        await using var v = await VaultFixture.CreateAsync(new Dictionary<string, string> { ["a.md"] = "Cliente", ["b.md"] = "cliente" });
        var handler = new SearchNotesHandler(v.Sessions);

        (await handler.HandleAsync(new SearchNotes(Id, "Cliente", new SearchOptions(MatchCase: true)), Ct)).Value.Select(h => h.Path).Should().Equal(P("a.md"));
        (await handler.HandleAsync(new SearchNotes(new VaultId(Guid.Parse("00000000-0000-0000-0000-0000000000ff")), "x"), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
    }
}
