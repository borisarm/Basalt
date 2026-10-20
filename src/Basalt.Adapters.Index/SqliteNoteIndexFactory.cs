using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Index;
using Basalt.Domain.Vaults;

namespace Basalt.Adapters.Index;

/// <summary>Opens <c>{AppData}/vaults/{id}/index.db</c>.</summary>
internal sealed class SqliteNoteIndexFactory(IVaultLocalDataStore localData) : INoteIndexFactory
{
    public async Task<INoteIndex> OpenAsync(VaultId vault, CancellationToken ct) =>
        await SqliteNoteIndex.OpenAsync(Path.Combine(localData.DirectoryOf(vault), "index.db"), ct).ConfigureAwait(false);
}
