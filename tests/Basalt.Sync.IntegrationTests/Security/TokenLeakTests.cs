using System.Text;
using Basalt.Adapters.GitHub;
using Basalt.Application.Auth;
using Basalt.Application.Common;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Sync;
using Basalt.Composition;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;
using Basalt.Testing;
using Basalt.Testing.FakeGitHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace Basalt.Sync.IntegrationTests.Security;

/// <summary>NFR-030: the token never reaches a file under <c>{AppData}</c> or a log line.</summary>
public sealed class TokenLeakTests : IAsyncLifetime
{
    private const string Token = "ghp_T0kenSearch_5f3a9c7e1b2d4f60";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly CommitAuthor Author = new("Test", "test@example.com");
    private static readonly GitHubAccountId Account = new(Guid.NewGuid());

    private readonly TempDirectory _appData = TempDirectory.Create("token-appdata");
    private readonly TempDirectory _root = TempDirectory.Create("token-vault");
    private ServiceProvider _provider = null!;
    private FakeGitHubServer _server = null!;
    private string _logs = null!;
    private RemoteRepositoryRef _reference = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await FakeGitHubServer.StartAsync(new FakeGitHubOptions { Token = Token }, Ct);
        _server.CreateRepo("acme", "vault");
        _logs = Path.Combine(_appData.Path, "logs");
        var services = new ServiceCollection()
            .AddLogging()
            .AddBasaltCore(_appData.Path);
        services.AddSingleton<ILoggerFactory>(_ => new SerilogLoggerFactory(
            new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(Path.Combine(_logs, "basalt-.log"), flushToDiskInterval: TimeSpan.FromMilliseconds(50))
                .CreateLogger(),
            dispose: true));
        _provider = services.BuildServiceProvider();
        _reference = new RemoteRepositoryRef(
            GitHubHost.ForTesting(new Uri(_server.BaseAddress)), "acme", "vault", "main");
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _server.DisposeAsync();
        await _appData.DisposeAsync();
        await _root.DisposeAsync();
    }

    [Fact]
    [Trait("Spec", "NFR-030")]
    public async Task Token_IsNotWrittenToAppDataLogsOrTheVault()
    {
        var (engine, vault) = await CreateDeviceAsync();
        (await engine.RunAsync(Request(vault), Ct)).Status.Should().Be(SyncCycleStatus.Published);
        (await engine.RunAsync(Request(vault), Ct)).Status.Should().Be(SyncCycleStatus.UpToDate);
        await _provider.DisposeAsync();

        Directory.Exists(_logs).Should().BeTrue("the cycle is logged");
        Directory.EnumerateFiles(_logs, "*.log").Should().NotBeEmpty();
        File.ReadAllText(Directory.EnumerateFiles(_logs, "*.log").First()).Should().Contain("GitHub");

        foreach (var directory in new[] { _appData.Path, _root.Path })
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                Contains(await File.ReadAllBytesAsync(file, Ct)).Should().BeFalse($"{file} must not contain the token");
            }
        }
    }

    private static bool Contains(byte[] content) =>
        content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Token)) >= 0;

    private SyncCycleRequest Request(VaultId vault) => new(
        Profile(vault), IsOnline: true, HasToken: true, Author, "Vault");

    private SyncProfile Profile(VaultId vault) => new(
        vault,
        Account,
        _reference,
        Enabled: true,
        Paused: false,
        new SyncTriggers(),
        SyncPolicy.Default,
        SyncScope.Default,
        new CommitSettings());

    private async Task<(VaultSyncEngine Engine, VaultId Id)> CreateDeviceAsync()
    {
        var location = Path.Combine(_root.Path, "device");
        var files = (await _provider.GetRequiredService<IVaultFileSystemFactory>().CreateAsync(location, Ct)).Value;
        var vault = new VaultId(Guid.NewGuid());
        (await _provider.GetRequiredService<IVaultConfigStore>()
            .SaveVaultConfigAsync(files, new VaultConfig(vault, VaultSettings.Default), Ct)).IsSuccess.Should().BeTrue();
        (await files.WriteAllBytesAsync(VaultPath.Create("A.md").Value, "# A"u8.ToArray(), null, Ct))
            .IsSuccess.Should().BeTrue();

        var tokens = new StubTokenProvider();
        var logging = new GitHubLoggingHandler(_provider.GetRequiredService<ILogger<GitHubLoggingHandler>>())
        {
            InnerHandler = new HttpClientHandler(),
        };
        var auth = new GitHubAuthHandler(tokens, Account) { InnerHandler = logging };
        var retry = new GitHubRetryHandler(new GitHubApiOptions { BaseDelay = TimeSpan.Zero }, TimeProvider.System)
        {
            InnerHandler = auth,
        };
        var http = new HttpClient(retry) { BaseAddress = new Uri(_server.BaseAddress) };
        var remote = new GitHubRemoteRepository(new GitHubApiClient(http, _reference, TimeProvider.System), _reference);

        var engine = new VaultSyncEngine(
            vault,
            files,
            remote,
            _provider.GetRequiredService<ISyncStateStoreFactory>().Create(vault),
            _provider.GetRequiredService<IEventBus>(),
            _provider.GetRequiredService<IClock>(),
            _provider.GetRequiredService<IDeviceInfo>(),
            TimeProvider.System,
            new SyncLocks(),
            new VaultLocks());
        return (engine, vault);
    }

    private sealed class StubTokenProvider : IGitHubTokenProvider
    {
        public Task<Result<GitHubToken>> GetTokenAsync(GitHubAccountId account, CancellationToken ct) =>
            Task.FromResult(Result<GitHubToken>.Success(new GitHubToken(Token)));

        public Task<Result<Unit>> StoreAsync(GitHubAccountId account, GitHubHost host, GitHubToken value, CancellationToken ct) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> RemoveAsync(GitHubAccountId account, CancellationToken ct) =>
            Task.FromResult(Result.Success());
    }
}
