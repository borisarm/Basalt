using System.Text;
using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Sync;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Testing.Fakes;

namespace Basalt.Application.Tests.Sync;

public sealed class LocalSnapshotScannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static LocalSnapshotScanner Scanner(
        IVaultFileSystem fileSystem,
        SyncScope? scope = null,
        SyncPolicy? policy = null,
        IgnoreRules? rules = null) =>
        new(fileSystem, scope ?? SyncScope.Default, policy ?? SyncPolicy.Default, rules ?? IgnoreRules.Default);

    private static async Task WriteAsync(InMemoryVaultFileSystem fileSystem, string path, string content) =>
        (await fileSystem.WriteAllBytesAsync(P(path), Encoding.UTF8.GetBytes(content), null, Ct)).IsSuccess.Should().BeTrue();

    private static BlobSha Hash(string content) => GitBlobHasher.Compute(Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task Scan_HashesIncludedFilesInOrdinalOrder()
    {
        var fileSystem = new InMemoryVaultFileSystem();

        await WriteAsync(fileSystem, "notes/B.md", "# B");
        await WriteAsync(fileSystem, "A.md", "# A");
        await WriteAsync(fileSystem, ".basalt/vault.json", "{}");

        var snapshot = await Scanner(fileSystem).ScanAsync(HashCache.Empty, null, Ct);

        snapshot.Manifest.Entries.Select(entry => entry.Path.ToString()).Should().Equal(".basalt/vault.json", "A.md", "notes/B.md");
        snapshot.Manifest.Get(P("A.md"))!.Value.Sha.Should().Be(Hash("# A"));
        snapshot.Manifest.Get(P("notes/B.md"))!.Value.Size.Should().Be(3);
        snapshot.Incidents.Should().BeEmpty();
        snapshot.ExcludedPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task Scan_ReusesACacheEntryWhenSizeAndModificationTimeMatch()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "A.md", "# A");
        var stat = (await fileSystem.StatAsync(P("A.md"), Ct)).Value;
        var cached = BlobSha.Parse("1111111111111111111111111111111111111111").Value;
        var cache = HashCache.Create([new HashCacheEntry(P("A.md"), stat.Size, stat.LastWriteUtc.UtcTicks, cached)]);

        var snapshot = await Scanner(fileSystem).ScanAsync(cache, null, Ct);

        snapshot.Manifest.Get(P("A.md"))!.Value.Sha.Should().Be(cached);
    }

    [Fact]
    public async Task Scan_RehashesWhenTheSizeOrTheModificationTimeChanges()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "A.md", "# A");
        var stat = (await fileSystem.StatAsync(P("A.md"), Ct)).Value;
        var cached = BlobSha.Parse("1111111111111111111111111111111111111111").Value;
        var otherTime = HashCache.Create([new HashCacheEntry(P("A.md"), stat.Size, stat.LastWriteUtc.UtcTicks + 1, cached)]);
        var otherSize = HashCache.Create([new HashCacheEntry(P("A.md"), stat.Size + 1, stat.LastWriteUtc.UtcTicks, cached)]);

        var byTime = await Scanner(fileSystem).ScanAsync(otherTime, null, Ct);
        var bySize = await Scanner(fileSystem).ScanAsync(otherSize, null, Ct);

        byTime.Manifest.Get(P("A.md"))!.Value.Sha.Should().Be(Hash("# A"));
        bySize.Manifest.Get(P("A.md"))!.Value.Sha.Should().Be(Hash("# A"));
    }

    [Fact]
    public async Task Scan_ExcludesTheDefaultIgnoredPaths()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, ".git/objects/ab/cdef", "git");
        await WriteAsync(fileSystem, ".basalt/cache/index.bin", "cache");
        await WriteAsync(fileSystem, ".basalt/workspace.device.json", "{}");
        await WriteAsync(fileSystem, ".DS_Store", "x");
        await WriteAsync(fileSystem, "notes/A.md", "A");

        var snapshot = await Scanner(fileSystem).ScanAsync(HashCache.Empty, null, Ct);

        snapshot.Manifest.Entries.Select(entry => entry.Path.ToString()).Should().Equal("notes/A.md");
        snapshot.ExcludedPaths.Should().Contain([P(".git/objects/ab/cdef"), P(".basalt/cache/index.bin"), P(".basalt/workspace.device.json"), P(".DS_Store")]);
    }

    [Fact]
    public async Task Scan_AppliesTheScope()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, ".obsidian/app.json", "{}");
        await WriteAsync(fileSystem, ".trash/Old.md", "old");
        await WriteAsync(fileSystem, "image.png", "png");
        await WriteAsync(fileSystem, "notes/A.md", "A");
        await WriteAsync(fileSystem, ".basalt/vault.json", "{}");

        var defaults = await Scanner(fileSystem).ScanAsync(HashCache.Empty, null, Ct);
        var everything = await Scanner(fileSystem, SyncScope.Default with
        {
            IncludeAttachments = true,
            IncludeObsidianConfig = true,
            IncludeTrash = true,
        }).ScanAsync(HashCache.Empty, null, Ct);

        defaults.Manifest.Entries.Select(entry => entry.Path.ToString())
            .Should().Equal(".basalt/vault.json", "image.png", "notes/A.md");
        everything.Manifest.Entries.Select(entry => entry.Path.ToString())
            .Should().Equal(".basalt/vault.json", ".obsidian/app.json", ".trash/Old.md", "image.png", "notes/A.md");
    }

    [Fact]
    public async Task Scan_AppendsTheProfileExtraPatternsAfterTheVaultIgnoreFile()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "draft.md", "draft");
        await WriteAsync(fileSystem, "keep.md", "keep");

        var snapshot = await Scanner(fileSystem, SyncScope.Default with { ExtraIgnorePatterns = ["draft.md"] })
            .ScanAsync(HashCache.Empty, null, Ct);

        snapshot.Manifest.Entries.Select(entry => entry.Path.ToString()).Should().Equal("keep.md");
    }

    [Fact]
    public async Task Scan_ExcludesFilesAboveTheLimitAndReportsAnIncident()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "big.bin", "0123456789");
        await WriteAsync(fileSystem, "small.md", "ok");

        var snapshot = await Scanner(fileSystem, policy: new SyncPolicy(MaxFileSizeBytes: 5)).ScanAsync(HashCache.Empty, null, Ct);

        snapshot.Manifest.Entries.Select(entry => entry.Path.ToString()).Should().Equal("small.md");
        snapshot.ExcludedPaths.Should().Contain(P("big.bin"));
        snapshot.Incidents.Should().ContainSingle(incident =>
            incident.Kind == SyncIncidentKind.TooLarge && incident.Path == P("big.bin"));
    }

    [Fact]
    public async Task Scan_ExcludesQuarantinedPathsAndTheirDescendants()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "folder/quarantined.md", "x");
        await WriteAsync(fileSystem, "folder/normal.md", "y");

        var snapshot = await Scanner(fileSystem).ScanAsync(HashCache.Empty, [P("folder/quarantined.md")], Ct);

        snapshot.Manifest.Entries.Select(entry => entry.Path.ToString()).Should().Equal("folder/normal.md");
        snapshot.ExcludedPaths.Should().Contain(P("folder/quarantined.md"));
    }

    [Fact]
    public async Task Scan_KeepsTheFirstOrdinalPathOnCaseCollisionsInCaseSensitiveFileSystems()
    {
        var fileSystem = new InMemoryVaultFileSystem(caseSensitive: true);
        await WriteAsync(fileSystem, "A.md", "upper");
        await WriteAsync(fileSystem, "a.md", "lower");

        var snapshot = await Scanner(fileSystem).ScanAsync(HashCache.Empty, null, Ct);

        snapshot.Manifest.Entries.Select(entry => entry.Path.ToString()).Should().Equal("A.md");
        snapshot.ExcludedPaths.Should().Contain(P("a.md"));
        snapshot.Incidents.Should().ContainSingle(incident =>
            incident.Kind == SyncIncidentKind.CaseCollision && incident.Path == P("a.md") && incident.OtherPath == P("A.md"));
    }

    [Fact]
    public async Task Scan_ReportsUnreadableFilesAndExcludesThem()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "A.md", "A");
        fileSystem.FailNextRead(P("A.md"), new Error(ErrorCodes.FsAccessDenied, "denied"));

        var snapshot = await Scanner(fileSystem).ScanAsync(HashCache.Empty, null, Ct);

        snapshot.Manifest.IsEmpty.Should().BeTrue();
        snapshot.ExcludedPaths.Should().Contain(P("A.md"));
        snapshot.Incidents.Should().ContainSingle(incident =>
            incident.Kind == SyncIncidentKind.Unreadable && incident.Path == P("A.md") && incident.Detail == ErrorCodes.FsAccessDenied);
    }

    [Fact]
    public async Task Scan_ReportsFilesThatChangedWhileBeingHashedAndExcludesThem()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "A.md", "abc");
        fileSystem.SetEnumeratedSize(P("A.md"), 10);

        var snapshot = await Scanner(fileSystem).ScanAsync(HashCache.Empty, null, Ct);

        snapshot.Manifest.IsEmpty.Should().BeTrue();
        snapshot.ExcludedPaths.Should().Contain(P("A.md"));
        snapshot.Incidents.Should().ContainSingle(incident =>
            incident.Kind == SyncIncidentKind.ChangedWhileScanning && incident.Path == P("A.md"));
    }

    [Fact]
    public async Task Scan_ExcludesSymbolicLinks()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "A.md", "A");
        fileSystem.SimulateSymbolicLink(P("link.md"), "A.md");

        var snapshot = await Scanner(fileSystem).ScanAsync(HashCache.Empty, null, Ct);

        snapshot.Manifest.Entries.Select(entry => entry.Path.ToString()).Should().Equal("A.md");
        snapshot.ExcludedPaths.Should().Contain(P("link.md"));
        snapshot.Incidents.Should().ContainSingle(incident =>
            incident.Kind == SyncIncidentKind.SymbolicLink && incident.Path == P("link.md"));
    }

    [Fact]
    public async Task Scan_OfAnEmptyVault_IsEmpty()
    {
        var fileSystem = new InMemoryVaultFileSystem();

        var snapshot = await Scanner(fileSystem).ScanAsync(null, null, Ct);

        snapshot.Manifest.Should().Be(Manifest.Empty);
        snapshot.HashCache.Count.Should().Be(0);
        snapshot.Incidents.Should().BeEmpty();
        snapshot.ExcludedPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task Scan_ReturnsACacheWithTheEntriesOfIncludedFiles()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "A.md", "A");
        await WriteAsync(fileSystem, "ignored.bin", "ignored");

        var snapshot = await Scanner(fileSystem, policy: new SyncPolicy(MaxFileSizeBytes: 5)).ScanAsync(HashCache.Empty, null, Ct);
        var entry = snapshot.HashCache.Entries.Should().ContainSingle().Subject;

        entry.Path.Should().Be(P("A.md"));
        entry.Sha.Should().Be(Hash("A"));
        entry.Size.Should().Be(1);
        entry.ModifiedUtcTicks.Should().Be((await fileSystem.StatAsync(P("A.md"), Ct)).Value.LastWriteUtc.UtcTicks);
    }

    [Fact]
    public async Task Scan_IsDeterministic()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        for (var i = 0; i < 20; i++)
        {
            await WriteAsync(fileSystem, $"notes/{i}.md", $"note {i}");
        }

        var first = await Scanner(fileSystem).ScanAsync(HashCache.Empty, null, Ct);
        var second = await Scanner(fileSystem).ScanAsync(first.HashCache, null, Ct);

        second.Manifest.Should().Be(first.Manifest);
        second.HashCache.Should().Be(first.HashCache);
    }
}
