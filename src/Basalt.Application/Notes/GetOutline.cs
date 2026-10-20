using Basalt.Application.Abstractions;
using Basalt.Application.Ports.Markdown;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Notes;

/// <summary>The heading tree of a note as saved (F-011); the editor computes it from its buffer with <see cref="OutlineBuilder"/>.</summary>
/// <returns><c>entry.not_found</c>.</returns>
public sealed record GetOutline(VaultId VaultId, VaultPath Path) : IQuery<IReadOnlyList<OutlineNode>>, IVaultRequest;

public sealed class GetOutlineHandler(IVaultSessions sessions, IMarkdownAnalyzer analyzer) : IQueryHandler<GetOutline, IReadOnlyList<OutlineNode>>
{
    public async Task<Result<IReadOnlyList<OutlineNode>>> HandleAsync(GetOutline query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var file = await NoteFiles.ReadAsync(session.Value.FileSystem, query.Path, ct).ConfigureAwait(false);
        return file.Map(f => OutlineBuilder.Build(analyzer.Analyze(f.Content.Text).Headings));
    }
}
