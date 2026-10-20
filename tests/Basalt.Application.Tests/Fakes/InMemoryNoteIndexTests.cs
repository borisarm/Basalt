using Basalt.Application.Ports.Index;
using Basalt.Domain.Vaults;
using Basalt.Testing.Contracts;
using Basalt.Testing.Fakes;

namespace Basalt.Application.Tests.Fakes;

public sealed class InMemoryNoteIndexTests : NoteIndexContract
{
    protected override Task<INoteIndex> CreateSutAsync() => Task.FromResult<INoteIndex>(new InMemoryNoteIndex());

    [Fact]
    public async Task Factory_ReturnsTheSameIndexPerVault()
    {
        var factory = new InMemoryNoteIndexFactory();
        var vault = new VaultId(Guid.Parse("00000000-0000-0000-0000-000000000001"));

        (await factory.OpenAsync(vault, Ct)).Should().BeSameAs(await factory.OpenAsync(vault, Ct));
    }
}
