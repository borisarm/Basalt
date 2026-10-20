using Basalt.Application.Errors;
using Basalt.Application.Graph;
using Basalt.Application.Tests.TestSupport;
using Basalt.Domain.Common;
using Basalt.Domain.Graph;
using Basalt.Domain.Vaults;
using static Basalt.Application.Tests.TestSupport.VaultFixture;

namespace Basalt.Application.Tests.Graph;

public sealed class GetGraphTests
{
    private static Task<VaultFixture> VaultAsync() => VaultFixture.CreateAsync(new Dictionary<string, string>
    {
        ["A.md"] = "[[B]] [[Inexistente]] #trabajo",
        ["B.md"] = "[[C]] ![[img.png]]",
        ["C.md"] = "",
        ["Sola.md"] = "",
        ["img.png"] = "",
    });

    private static List<string> Ids(GraphModel graph) => [.. graph.Nodes.Select(n => n.Id)];

    [Fact]
    [Trait("Spec", "F-012.1")]
    public async Task LocalGraph_DepthOne()
    {
        await using var v = await VaultAsync();

        var graph = await new GetGraphHandler(v.Sessions).HandleAsync(
            new GetLocalGraph(Id, P("A.md"), 1, LocalGraphDirection.Both, GraphOptions.Default with { ShowUnresolved = false }), Ct);

        Ids(graph.Value).Should().Equal("A.md", "B.md");
    }

    [Fact]
    [Trait("Spec", "F-012.2")]
    public async Task GlobalGraph_ShowsUnresolvedUnlessExistingOnly()
    {
        await using var v = await VaultAsync();
        var handler = new GetGraphHandler(v.Sessions);

        var all = (await handler.HandleAsync(new GetGraph(Id, GraphOptions.Default), Ct)).Value;
        var existing = (await handler.HandleAsync(new GetGraph(Id, GraphOptions.Default with { ShowUnresolved = false }), Ct)).Value;

        all.Nodes.Should().Contain(n => n.Kind == GraphNodeKind.Unresolved && n.Label == "Inexistente");
        existing.Nodes.Should().NotContain(n => n.Kind == GraphNodeKind.Unresolved);
    }

    [Fact]
    public async Task OptionsReachTheBuilder_AndQueriesFilterAndColour()
    {
        await using var v = await VaultAsync();
        var handler = new GetGraphHandler(v.Sessions);

        var graph = (await handler.HandleAsync(new GetGraph(Id, new GraphOptions(
            ShowOrphans: false, ShowAttachments: true, ShowUnresolved: false, ShowTags: true, Filter: "-file:Sola", ColorGroups: ["tag:#trabajo", "file:C"])), Ct)).Value;

        Ids(graph).Should().Equal("A.md", "B.md", "C.md", "img.png", "tag:TRABAJO");
        graph.Nodes.Select(n => n.Group).Should().Equal(0, -1, 1, -1, -1);
    }

    [Fact]
    public async Task LocalGraph_ClampsTheDepth()
    {
        await using var v = await VaultAsync();
        var handler = new GetGraphHandler(v.Sessions);

        Ids((await handler.HandleAsync(new GetLocalGraph(Id, P("A.md"), 0, LocalGraphDirection.Both, GraphOptions.Default with { ShowUnresolved = false }), Ct)).Value)
            .Should().Equal("A.md", "B.md");
        Ids((await handler.HandleAsync(new GetLocalGraph(Id, P("A.md"), 99, LocalGraphDirection.Outgoing, GraphOptions.Default with { ShowUnresolved = false }), Ct)).Value)
            .Should().Equal("A.md", "B.md", "C.md");
    }

    [Fact]
    public async Task InvalidQueries_AndClosedVaults_AreErrors()
    {
        await using var v = await VaultAsync();
        var handler = new GetGraphHandler(v.Sessions);
        var closed = new VaultId(Guid.Parse("00000000-0000-0000-0000-0000000000ff"));

        (await handler.HandleAsync(new GetGraph(Id, GraphOptions.Default with { Filter = "/[/" }), Ct)).Error.Code.Should().Be(DomainErrors.SearchInvalidRegex);
        (await handler.HandleAsync(new GetGraph(Id, GraphOptions.Default with { ColorGroups = ["/(/"] }), Ct)).Error.Code.Should().Be(DomainErrors.SearchInvalidRegex);
        (await handler.HandleAsync(new GetGraph(closed, GraphOptions.Default), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
        (await handler.HandleAsync(new GetLocalGraph(closed, P("A.md"), 1, LocalGraphDirection.Both, GraphOptions.Default), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
    }
}
