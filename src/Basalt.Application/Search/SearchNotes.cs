using Basalt.Application.Abstractions;
using Basalt.Application.Ports.Index;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Search;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Search;

/// <summary>Full-text search of a vault (F-007, spec 08 §4).</summary>
/// <returns><c>search.invalid_regex</c> / <c>search.too_complex</c> for queries that cannot be parsed (F-007.4).</returns>
public sealed record SearchNotes(VaultId VaultId, string Query, SearchOptions? Options = null) : IQuery<IReadOnlyList<SearchHit>>, IVaultRequest;

public sealed class SearchNotesHandler(IVaultSessions sessions) : IQueryHandler<SearchNotes, IReadOnlyList<SearchHit>>
{
    public async Task<Result<IReadOnlyList<SearchHit>>> HandleAsync(SearchNotes query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var parsed = SearchQueryParser.Parse(query.Query);
        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        var hits = await session.Value.Get<INoteIndexReader>().SearchAsync(parsed.Value, query.Options ?? SearchOptions.Default, ct).ConfigureAwait(false);
        return Result.Success(hits);
    }
}
