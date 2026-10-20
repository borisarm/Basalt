using Basalt.Application.Ports.Remote;
using Basalt.Domain.Accounts;
using Basalt.Testing.Contracts;
using Basalt.Testing.Remote;
using Microsoft.Extensions.Time.Testing;

namespace Basalt.Adapters.GitHub.Tests;

public sealed class InMemoryRemoteRepositoryTests : RemoteRepositoryContract
{
    protected override Task<IRemoteRepository> CreateSutAsync() =>
        Task.FromResult<IRemoteRepository>(new InMemoryRemoteRepository(
            new RemoteRepositoryRef(GitHubHost.GitHubDotCom, "acme", "vault", "main"),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero))));
}
