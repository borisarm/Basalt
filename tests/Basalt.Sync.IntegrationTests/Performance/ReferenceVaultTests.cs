using System.Diagnostics;
using System.Security.Cryptography;
using Basalt.Adapters.FileSystem;
using Basalt.Adapters.Index;
using Basalt.Adapters.Markdown;
using Basalt.Adapters.Platform;
using Basalt.Application.Events;
using Basalt.Application.Indexing;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Index;
using Basalt.Application.Ports.Markdown;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Search;
using Basalt.Application.Sync;
using Basalt.Application.Vaults;
using Basalt.Domain.Sync;
using Basalt.Testing;
using Basalt.VaultGenerator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basalt.Sync.IntegrationTests.Performance;

public sealed class ReferenceVaultTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Generator_IsDeterministic()
    {
        await using var a = TempDirectory.Create("gen-a");
        await using var b = TempDirectory.Create("gen-b");
        var options = new VaultGeneratorOptions(Seed: 7, Notes: 120, Attachments: 3, Folders: 12);

        var pathsA = ReferenceVaultGenerator.Generate(a.Path, options);
        var pathsB = ReferenceVaultGenerator.Generate(b.Path, options);

        pathsA.Should().Equal(pathsB);
        foreach (var path in pathsA)
        {
            Hash(a, path).Should().Be(Hash(b, path));
        }

        pathsA.Select(p => p.Split('/').Length - 1).Max().Should().BeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task Generator_ProducesLinksTagsAndFrontmatter()
    {
        await using var directory = TempDirectory.Create("gen");
        var paths = ReferenceVaultGenerator.Generate(directory.Path, new VaultGeneratorOptions(Notes: 200, Attachments: 0, Folders: 20));
        var analyzer = new ServiceCollection().AddBasaltMarkdownAdapter().BuildServiceProvider().GetRequiredService<IMarkdownAnalyzer>();

        var metadata = paths.Select(p => analyzer.Analyze(File.ReadAllText(Path.Combine([directory.Path, .. p.Split('/')])))).ToList();

        metadata.Average(m => m.Links.Count).Should().BeInRange(1, 10);
        metadata.Average(m => m.Tags.Count).Should().BeInRange(0.5, 5);
        metadata.Should().Contain(m => m.Frontmatter != null);
    }

    [Fact]
    [Trait("Category", "Performance")]
    [Trait("Spec", "NFR-002")]
    public async Task OpeningTheReferenceVault_WithAnExistingIndex_IsFast()
    {
        await using var root = TempDirectory.Create("refvault");
        var vaultFolder = Path.Combine(root.Path, "Reference");
        ReferenceVaultGenerator.Generate(vaultFolder, new VaultGeneratorOptions(Attachments: 50));
        await using var services = new ServiceCollection()
            .AddBasaltFileSystemAdapter()
            .AddBasaltMarkdownAdapter()
            .AddBasaltIndexAdapter()
            .AddBasaltPlatformAdapter(Path.Combine(root.Path, "appdata"))
            .BuildServiceProvider();
        using var registry = new VaultRegistry(services.GetRequiredService<IDocumentStore<VaultRegistryDocument>>());
        var registration = (await new OpenFolderAsVaultHandler(
            services.GetRequiredService<IVaultFileSystemFactory>(), services.GetRequiredService<IVaultConfigStore>(), registry,
            services.GetRequiredService<IIdGenerator>(), services.GetRequiredService<IClock>()).HandleAsync(new OpenFolderAsVault(vaultFolder), Ct)).Value;

        // NFR-003: first open builds the whole index.
        var initial = Stopwatch.StartNew();
        await OpenAndWaitAsync(services, registry, registration.Id, waitForScan: true);
        initial.Stop();

        // NFR-002: re-open with the index in place, until the vault is usable.
        var reopen = Stopwatch.StartNew();
        await OpenAndWaitAsync(services, registry, registration.Id, waitForScan: false);
        reopen.Stop();

        TestContext.Current.SendDiagnosticMessage($"Initial indexing {initial.Elapsed}, re-open {reopen.Elapsed}");
        initial.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60));
        reopen.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    [Trait("Category", "Performance")]
    [Trait("Spec", "NFR-010")]
    public async Task LocalScanOfTheReferenceVault_WithAValidHashCache_IsFast()
    {
        await using var root = TempDirectory.Create("refvault-scan");
        var vaultFolder = Path.Combine(root.Path, "Reference");
        ReferenceVaultGenerator.Generate(vaultFolder, new VaultGeneratorOptions());
        await using var services = new ServiceCollection().AddBasaltFileSystemAdapter().BuildServiceProvider();
        var fileSystem = (await services.GetRequiredService<IVaultFileSystemFactory>().OpenAsync(vaultFolder, Ct)).Value;
        var scanner = new LocalSnapshotScanner(fileSystem, SyncScope.Default, SyncPolicy.Default, IgnoreRules.Default);

        var cold = Stopwatch.StartNew();
        var first = await scanner.ScanAsync(HashCache.Empty, null, Ct);
        cold.Stop();

        var warm = Stopwatch.StartNew();
        var second = await scanner.ScanAsync(first.HashCache, null, Ct);
        warm.Stop();

        TestContext.Current.SendDiagnosticMessage(
            $"Local scan: cold {cold.Elapsed}, with cache {warm.Elapsed} ({second.Manifest.Count} files)");
        second.Manifest.Should().Be(first.Manifest);
        second.Manifest.Count.Should().BeGreaterThan(10_000);
        second.Incidents.Should().BeEmpty();
        warm.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    private static async Task OpenAndWaitAsync(ServiceProvider services, VaultRegistry registry, Domain.Vaults.VaultId id, bool waitForScan)
    {
        await using var sessions = new VaultSessions();
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var initializer = new IndexingSessionInitializer(
            services.GetRequiredService<INoteIndexFactory>(), services.GetRequiredService<IMarkdownAnalyzer>(), events, TimeProvider.System, NullLoggerFactory.Instance);
        var open = new OpenVaultHandler(registry, services.GetRequiredService<IVaultFileSystemFactory>(), services.GetRequiredService<IVaultConfigStore>(),
            sessions, [initializer], events, services.GetRequiredService<IClock>());

        (await open.HandleAsync(new OpenVault(id), Ct)).IsSuccess.Should().BeTrue();
        var session = sessions.Get(id).Value;
        if (waitForScan)
        {
            await session.Get<VaultIndexer>().ScanCompletion;
        }

        session.Get<IMetadataCache>().Current.Files.Should().HaveCountGreaterThan(10_000);
        if (waitForScan)
        {
            // F-007.5: a term search over 10 000 notes.
            var search = new SearchNotesHandler(sessions);
            var timings = new Dictionary<string, TimeSpan>();
            foreach (var term in new[] { "Nota 01234", "\"magna aliqua\"", "veniam" })
            {
                var watch = Stopwatch.StartNew();
                var hits = (await search.HandleAsync(new SearchNotes(id, term, new SearchOptions(Limit: 50)), Ct)).Value;
                timings[term] = watch.Elapsed;
                hits.Should().NotBeEmpty();
            }

            var report = string.Join(", ", timings.Select(t => $"{t.Key}: {t.Value.TotalMilliseconds:F0} ms"));
            TestContext.Current.SendDiagnosticMessage("Search " + report);
            timings.Values.Should().AllSatisfy(t => t.Should().BeLessThan(TimeSpan.FromMilliseconds(300)), report);
        }
    }

    private static string Hash(TempDirectory directory, string relative) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine([directory.Path, .. relative.Split('/')]))));
}
