using Basalt.Application.Abstractions;
using Basalt.Application.Indexing;
using Basalt.Application.Ports.Index;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Graph;
using Basalt.Domain.Notes;
using Basalt.Domain.Search;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Graph;

/// <summary>
/// Graph options (F-012): visibility filters, an optional search query restricting files and colour groups given as
/// search queries (first match wins).
/// </summary>
public sealed record GraphOptions(
    bool ShowOrphans = true,
    bool ShowAttachments = false,
    bool ShowUnresolved = true,
    bool ShowTags = false,
    string? Filter = null,
    IReadOnlyList<string>? ColorGroups = null)
{
    public static GraphOptions Default { get; } = new();
}

/// <summary>The global graph.</summary>
/// <returns><c>search.*</c> errors when the filter or a colour group query is invalid.</returns>
public sealed record GetGraph(VaultId VaultId, GraphOptions Options) : IQuery<GraphModel>, IVaultRequest;

/// <summary>The local graph of a note (F-012.1); <paramref name="Depth"/> is clamped to 1–5.</summary>
public sealed record GetLocalGraph(VaultId VaultId, VaultPath Center, int Depth, LocalGraphDirection Direction, GraphOptions Options) : IQuery<GraphModel>, IVaultRequest;

public sealed class GetGraphHandler(IVaultSessions sessions) : IQueryHandler<GetGraph, GraphModel>, IQueryHandler<GetLocalGraph, GraphModel>
{
    public const int MinDepth = 1;
    public const int MaxDepth = 5;

    public Task<Result<GraphModel>> HandleAsync(GetGraph query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return BuildAsync(query.VaultId, query.Options, (files, links, filter) => GraphBuilder.Build(files, links, filter), ct);
    }

    public Task<Result<GraphModel>> HandleAsync(GetLocalGraph query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var depth = Math.Clamp(query.Depth, MinDepth, MaxDepth);
        return BuildAsync(query.VaultId, query.Options, (files, links, filter) => GraphBuilder.BuildLocal(files, links, query.Center, depth, query.Direction, filter), ct);
    }

    private async Task<Result<GraphModel>> BuildAsync(
        VaultId vault, GraphOptions options, Func<IEnumerable<GraphFile>, Domain.Links.LinkGraph, GraphFilter, GraphModel> build, CancellationToken ct)
    {
        var session = sessions.Get(vault);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var index = session.Value.Get<INoteIndexReader>();
        IReadOnlySet<VaultPath>? include = null;
        if (!string.IsNullOrWhiteSpace(options.Filter))
        {
            var matching = await MatchingAsync(index, options.Filter, ct).ConfigureAwait(false);
            if (matching.IsFailure)
            {
                return matching.Error;
            }

            include = matching.Value;
        }

        var groups = new List<IReadOnlySet<VaultPath>>();
        foreach (var group in options.ColorGroups ?? [])
        {
            var matching = await MatchingAsync(index, group, ct).ConfigureAwait(false);
            if (matching.IsFailure)
            {
                return matching.Error;
            }

            groups.Add(matching.Value);
        }

        var snapshot = session.Value.Get<IMetadataCache>().Current;
        var files = snapshot.Files.Values.Select(f => new GraphFile(f.Path, f.Kind, [.. f.Metadata.DistinctTags]));
        var filter = new GraphFilter(options.ShowOrphans, options.ShowAttachments, options.ShowUnresolved, options.ShowTags, include, groups);
        return build(files, snapshot.Graph, filter);
    }

    private static async Task<Result<IReadOnlySet<VaultPath>>> MatchingAsync(INoteIndexReader index, string query, CancellationToken ct)
    {
        var parsed = SearchQueryParser.Parse(query);
        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        var hits = await index.SearchAsync(parsed.Value, SearchOptions.Default, ct).ConfigureAwait(false);
        return Result.Success<IReadOnlySet<VaultPath>>(hits.Select(h => h.Path).ToHashSet());
    }
}
