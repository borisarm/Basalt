using System.Diagnostics;
using System.Text;
using Basalt.Adapters.GitHub;
using Basalt.Application.Common;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Sync;
using Basalt.Composition;
using Basalt.Domain.Accounts;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;
using Basalt.Testing;
using Basalt.Testing.FakeGitHub;
using Basalt.Testing.Fakes;
using Basalt.VaultGenerator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Basalt.Sync.IntegrationTests.Performance;

/// <summary>NFR-009 and NFR-011: sync transport against a real HTTP FakeGitHub.</summary>
public sealed class SyncTransportPerformanceTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly CommitAuthor Author = new("Test", "test@example.com");

    private readonly TempDirectory _appData = TempDirectory.Create("transport-appdata");
    private readonly TempDirectory _root = TempDirectory.Create("transport-vault");
    private ServiceProvider _provider = null!;
    private FakeGitHubServer _server = null!;
    private RemoteRepositoryRef _reference = null!;
    private IRemoteRepository _remote = null!;

    public async ValueTask InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddBasaltCore(_appData.Path)
            .BuildServiceProvider();
        _server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        _server.CreateRepo("acme", "vault");
        var http = _server.CreateClient(_server.Options.Token);
        _reference = new RemoteRepositoryRef(
            GitHubHost.ForTesting(new Uri(_server.BaseAddress)), "acme", "vault", "main");
        _remote = new GitHubRemoteRepository(new GitHubApiClient(http, _reference, TimeProvider.System), _reference);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _server.DisposeAsync();
        await _appData.DisposeAsync();
        await _root.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Performance")]
    [Trait("Spec", "NFR-009")]
    public async Task NoChangeCycle_With304FinishesWithin1s()
    {
        var (engine, files, vault) = await CreateDeviceAsync("laptop");
        await WriteAsync(files, "Notes/A.md", "# A");
        var first = await engine.RunAsync(Request(vault), Ct);
        first.Status.Should().Be(SyncCycleStatus.Published);

        var watch = Stopwatch.StartNew();
        var second = await engine.RunAsync(Request(vault), Ct);
        watch.Stop();

        second.Status.Should().Be(SyncCycleStatus.UpToDate);
        _server.Requests.Should().Contain(request => request.Path.EndsWith("/git/ref/heads/main", StringComparison.Ordinal));
        TestContext.Current.SendDiagnosticMessage($"No-change sync cycle: {watch.Elapsed.TotalMilliseconds:F0} ms");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    [Trait("Category", "Performance")]
    [Trait("Spec", "NFR-011")]
    public async Task CloneOfTheReferenceVault_ThroughTheZipball_FinishesWithin60s()
    {
        await using var generated = TempDirectory.Create("transport-reference");
        var paths = ReferenceVaultGenerator.Generate(generated.Path, new VaultGeneratorOptions());
        var files = new Dictionary<string, byte[]>(paths.Count, StringComparer.Ordinal);
        foreach (var path in paths)
        {
            files[path] = await File.ReadAllBytesAsync(Path.Combine([generated.Path, .. path.Split('/')]), Ct);
        }

        _server.CommitFiles("acme", "vault", "main", files, "reference vault");
        var (engine, local, vault) = await CreateInMemoryDeviceAsync();

        var watch = Stopwatch.StartNew();
        var result = await engine.RunAsync(Request(vault), Ct);
        watch.Stop();

        result.Status.Should().BeOneOf(SyncCycleStatus.Published, SyncCycleStatus.Downloaded);
        (await CountFilesAsync(local)).Should().BeGreaterThanOrEqualTo(paths.Count);
        TestContext.Current.SendDiagnosticMessage(
            $"Zipball clone of {paths.Count} files: {watch.Elapsed.TotalSeconds:F1} s");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60));
    }

    private SyncCycleRequest Request(VaultId vault) => new(
        Profile(vault),
        IsOnline: true,
        HasToken: true,
        Author,
        "Vault");

    private SyncProfile Profile(VaultId vault) => new(
        vault,
        new GitHubAccountId(Guid.NewGuid()),
        _reference,
        true,
        false,
        new SyncTriggers(),
        SyncPolicy.Default,
        SyncScope.Default,
        new CommitSettings());

    private async Task<(VaultSyncEngine Engine, IVaultFileSystem Files, VaultId Id)> CreateDeviceAsync(string name)
    {
        var location = Path.Combine(_root.Path, name);
        var files = (await _provider.GetRequiredService<IVaultFileSystemFactory>().CreateAsync(location, Ct)).Value;
        var vault = new VaultId(Guid.NewGuid());
        (await _provider.GetRequiredService<IVaultConfigStore>()
            .SaveVaultConfigAsync(files, new VaultConfig(vault, VaultSettings.Default), Ct)).IsSuccess.Should().BeTrue();
        var state = _provider.GetRequiredService<ISyncStateStoreFactory>().Create(vault);
        var engine = new VaultSyncEngine(
            vault,
            files,
            _remote,
            state,
            _provider.GetRequiredService<IEventBus>(),
            _provider.GetRequiredService<IClock>(),
            _provider.GetRequiredService<IDeviceInfo>(),
            TimeProvider.System,
            new SyncLocks(),
            new VaultLocks());
        return (engine, files, vault);
    }

    // NFR-011 measures the transport and zipball extraction; the validated in-memory file system removes disk speed
    // from the equation (the real one is covered by NFR-010 and the file-system contract).
    private async Task<(VaultSyncEngine Engine, IVaultFileSystem Files, VaultId Id)> CreateInMemoryDeviceAsync()
    {
        var files = new InMemoryVaultFileSystem();
        var vault = new VaultId(Guid.NewGuid());
        (await _provider.GetRequiredService<IVaultConfigStore>()
            .SaveVaultConfigAsync(files, new VaultConfig(vault, VaultSettings.Default), Ct)).IsSuccess.Should().BeTrue();
        var state = _provider.GetRequiredService<ISyncStateStoreFactory>().Create(vault);
        var engine = new VaultSyncEngine(
            vault,
            files,
            _remote,
            state,
            _provider.GetRequiredService<IEventBus>(),
            _provider.GetRequiredService<IClock>(),
            _provider.GetRequiredService<IDeviceInfo>(),
            TimeProvider.System,
            new SyncLocks(),
            new VaultLocks());
        return (engine, files, vault);
    }

    private static async Task WriteAsync(IVaultFileSystem files, string path, string content)
    {
        (await files.WriteAllBytesAsync(VaultPath.Create(path).Value, Encoding.UTF8.GetBytes(content), null, Ct))
            .IsSuccess.Should().BeTrue();
    }

    private static async Task<int> CountFilesAsync(IVaultFileSystem files)
    {
        var count = 0;
        await foreach (var entry in files.EnumerateAsync(VaultPath.Root, recursive: true, Ct))
        {
            if (!entry.IsDirectory)
            {
                count++;
            }
        }

        return count;
    }
}
