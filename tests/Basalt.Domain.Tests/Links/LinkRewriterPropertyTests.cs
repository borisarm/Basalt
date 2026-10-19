using System.Text.RegularExpressions;
using Basalt.Domain.Links;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Testing.Catalogs;
using Basalt.Testing.Documents;
using Basalt.Testing.Generators;
using FsCheck;
using FsCheck.Fluent;

namespace Basalt.Domain.Tests.Links;

public sealed partial class LinkRewriterPropertyTests
{
    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static (RewriteResult Result, InMemoryFileCatalog After, IReadOnlyDictionary<VaultPath, VaultPath> Map) Run(RewriteScenario scenario)
    {
        var map = scenario.Renames.ToDictionary(kv => P(kv.Key), kv => P(kv.Value));
        var before = new InMemoryFileCatalog(scenario.Files);
        var after = new InMemoryFileCatalog(scenario.Files.Select(f => scenario.Renames.TryGetValue(f, out var to) ? to : f));
        var request = new RewriteRequest(P(scenario.Source), P(scenario.Source), map, before, after, VaultSettings.Default);
        return (LinkRewriter.Rewrite(scenario.Document, WikiLinkScanner.Scan(scenario.Document), request), after, map);
    }

    [Fact]
    [Trait("Spec", "DOM-I2")]
    public void Rewrite_TextOutsideLinks_IsUnchanged()
    {
        Prop.ForAll(RewriteScenario.Arbitrary(), scenario =>
        {
            var (result, _, _) = Run(scenario);
            return StripLinks(result.Text) == StripLinks(scenario.Document);
        }).QuickCheckThrowOnFailure();
    }

    [Fact]
    [Trait("Spec", "DOM-5.2")]
    public void Rewrite_IsIdempotent()
    {
        Prop.ForAll(RewriteScenario.Arbitrary(), scenario =>
        {
            var (result, after, _) = Run(scenario);
            var again = LinkRewriter.Rewrite(
                result.Text,
                WikiLinkScanner.Scan(result.Text),
                new RewriteRequest(P(scenario.Source), P(scenario.Source), new Dictionary<VaultPath, VaultPath>(), after, after, VaultSettings.Default));
            return again.Text == result.Text;
        }).QuickCheckThrowOnFailure();
    }

    [Fact]
    [Trait("Spec", "DOM-5.2")]
    public void Rewrite_EveryResolvedLinkStillPointsToItsRenamedTarget()
    {
        Prop.ForAll(RewriteScenario.Arbitrary(), scenario =>
        {
            var (result, after, map) = Run(scenario);
            var before = new InMemoryFileCatalog(scenario.Files);
            var oldLinks = WikiLinkScanner.Scan(scenario.Document);
            var newLinks = WikiLinkScanner.Scan(result.Text);
            return oldLinks.Count == newLinks.Count && oldLinks.Zip(newLinks).All(pair =>
            {
                var old = LinkResolver.Resolve(pair.First, P(scenario.Source), before).TargetPath;
                if (old is null)
                {
                    return pair.First.RawTarget == pair.Second.RawTarget;
                }

                var expected = map.TryGetValue(old.Value, out var to) ? to : old.Value;
                return LinkResolver.Resolve(pair.Second, P(scenario.Source), after).TargetPath == expected
                    && pair.First.Alias == pair.Second.Alias
                    && pair.First.Subpath == pair.Second.Subpath;
            });
        }).QuickCheckThrowOnFailure();
    }

    [Fact]
    public void Generator_ProducesScenariosThatActuallyRewriteLinks()
    {
        var scenarios = RewriteScenario.Generator().Sample(20, 200);

        scenarios.Count(s => Run(s).Result.ChangedLinks > 0).Should().BeGreaterThan(4);
        scenarios.Should().Contain(s => s.Renames.Count == 0);
    }

    private static string StripLinks(string text) => LinkRegex().Replace(text, "§");

    [GeneratedRegex(@"!?\[\[[^\[\]\n]*\]\]")]
    private static partial Regex LinkRegex();
}
