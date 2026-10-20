using Basalt.Application.Abstractions;
using Basalt.Application.Indexing;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Search;
using Basalt.Domain.Tags;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Tags;

/// <summary>The tag tree of a vault; each note counts once per tag (F-006.1).</summary>
public sealed record GetTags(VaultId VaultId) : IQuery<TagHierarchy>, IVaultRequest;

public sealed class GetTagsHandler(IVaultSessions sessions) : IQueryHandler<GetTags, TagHierarchy>
{
    public Task<Result<TagHierarchy>> HandleAsync(GetTags query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        return Task.FromResult(session.IsFailure
            ? Result.Failure<TagHierarchy>(session.Error)
            : Result.Success(TagHierarchies.Of(session.Value.Get<IMetadataCache>().Current)));
    }
}

public sealed record TagSuggestion(string Name, int Count, IReadOnlyList<int> Positions, double Score);

/// <summary>Autocompletion after <c>#</c>: fuzzy over full tag names, then by number of notes, then by name.</summary>
public sealed record SuggestTags(VaultId VaultId, string Partial, int Limit = 50) : IQuery<IReadOnlyList<TagSuggestion>>, IVaultRequest;

public sealed class SuggestTagsHandler(IVaultSessions sessions) : IQueryHandler<SuggestTags, IReadOnlyList<TagSuggestion>>
{
    public Task<Result<IReadOnlyList<TagSuggestion>>> HandleAsync(SuggestTags query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return Task.FromResult(Result.Failure<IReadOnlyList<TagSuggestion>>(session.Error));
        }

        var partial = query.Partial.TrimStart('#');
        var suggestions = Flatten(TagHierarchies.Of(session.Value.Get<IMetadataCache>().Current).Roots)
            .Select(n => (Node: n, Match: FuzzyMatcher.Match(partial, n.FullName)))
            .Where(x => x.Match is not null)
            .OrderByDescending(x => x.Match!.Score)
            .ThenByDescending(x => x.Node.Count)
            .ThenBy(x => x.Node.FullName, StringComparer.Ordinal)
            .Take(query.Limit)
            .Select(x => new TagSuggestion(x.Node.FullName, x.Node.Count, x.Match!.Positions, x.Match.Score));
        return Task.FromResult(Result.Success<IReadOnlyList<TagSuggestion>>([.. suggestions]));
    }

    private static IEnumerable<TagNode> Flatten(IEnumerable<TagNode> nodes) => nodes.SelectMany(n => Flatten(n.Children).Prepend(n));
}

internal static class TagHierarchies
{
    public static TagHierarchy Of(MetadataSnapshot snapshot) => TagHierarchy.Build(snapshot.Files.Values.SelectMany(f => f.Metadata.DistinctTags));
}
