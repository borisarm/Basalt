using Basalt.Adapters.FileSystem;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Sync;
using Basalt.Domain.Sync;
using Basalt.Testing;
using Basalt.VaultGenerator;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Basalt.Benchmarks;

/// <summary>NFR-010: a local sync scan of the reference vault with a valid hash cache must stay under 2 s.</summary>
[MemoryDiagnoser]
public class LocalScanBenchmarks
{
    private ServiceProvider _services = null!;
    private TempDirectory _directory = null!;
    private LocalSnapshotScanner _scanner = null!;
    private HashCache _cache = HashCache.Empty;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directory = TempDirectory.Create("bench-scan");
        ReferenceVaultGenerator.Generate(_directory.Path, new VaultGeneratorOptions());
        _services = new ServiceCollection().AddBasaltFileSystemAdapter().BuildServiceProvider();
        var files = (await _services.GetRequiredService<IVaultFileSystemFactory>()
            .OpenAsync(_directory.Path, CancellationToken.None)).Value;
        _scanner = new LocalSnapshotScanner(files, SyncScope.Default, SyncPolicy.Default, IgnoreRules.Default);
        _cache = (await _scanner.ScanAsync(HashCache.Empty, null, CancellationToken.None)).HashCache;
    }

    [Benchmark]
    public Task<LocalSnapshot> ScanWithCacheAsync() => _scanner.ScanAsync(_cache, null, CancellationToken.None);

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _services.DisposeAsync();
        await _directory.DisposeAsync();
    }
}
