using Basalt.Application.Ports.FileSystem;
using Basalt.Testing.Contracts;
using Basalt.Testing.Fakes;

namespace Basalt.Application.Tests.Fakes;

public sealed class InMemoryFileWatcherTests : FileWatcherContract
{
    private InMemoryVaultFileSystem Fs => (InMemoryVaultFileSystem)Sut;

    protected override Task<IVaultFileSystem> CreateSutAsync() => Task.FromResult<IVaultFileSystem>(new InMemoryVaultFileSystem());

    protected override Task ExternalWriteAsync(string path, string content)
    {
        if (!VaultFileConventions.IsTemporaryFile(P(path).Name))
        {
            Fs.SimulateExternalWrite(P(path), content);
        }

        return Task.CompletedTask;
    }

    protected override Task ExternalDeleteAsync(string path)
    {
        Fs.SimulateExternalDelete(P(path));
        return Task.CompletedTask;
    }

    protected override Task ExternalRenameAsync(string from, string to)
    {
        Fs.SimulateExternalRename(P(from), P(to));
        return Task.CompletedTask;
    }
}
