using System.Text;
using Basalt.Adapters.Markdown;
using Basalt.Application.Events;
using Basalt.Application.Indexing;
using Basalt.Application.Ports.Markdown;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Vaults;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basalt.Application.Tests.TestSupport;

/// <summary>An open vault over an in-memory file system with the real Markdown adapter and indexing.</summary>
public sealed class VaultFixture : IAsyncDisposable
{
    public static readonly VaultId Id = new(Guid.Parse("00000000-0000-0000-0000-0000000000aa"));

    private static readonly ServiceProvider Markdown = new ServiceCollection().AddBasaltMarkdownAdapter().BuildServiceProvider();

    private VaultFixture(VaultSettings settings)
    {
        FileSystem = new InMemoryVaultFileSystem(time: Clock.Time);
        Session = new VaultSession(Id, "Test", FileSystem, settings);
        Sessions.Add(Session);
    }

    public FakeClock Clock { get; } = new();

    public InMemoryVaultFileSystem FileSystem { get; }

    public InMemoryEventBus Events { get; } = new(NullLogger<InMemoryEventBus>.Instance);

    public VaultSessions Sessions { get; } = new();

    public VaultSession Session { get; }

    public IMarkdownAnalyzer Analyzer => Markdown.GetRequiredService<IMarkdownAnalyzer>();

    public IFrontmatterEditor FrontmatterEditor => Markdown.GetRequiredService<IFrontmatterEditor>();

    public ICultureProvider Culture { get; } = new FakeCultureProvider();

    public MetadataSnapshot Cache => Session.Get<IMetadataCache>().Current;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static VaultPath P(string path) => VaultPath.Create(path).Value;

    /// <summary>Creates files, opens the vault and waits for the initial index.</summary>
    public static async Task<VaultFixture> CreateAsync(IReadOnlyDictionary<string, string>? files = null, VaultSettings? settings = null)
    {
        var fixture = new VaultFixture(settings ?? VaultSettings.Default);
        foreach (var (path, text) in files ?? new Dictionary<string, string>())
        {
            await fixture.FileSystem.WriteAllBytesAsync(P(path), Encoding.UTF8.GetBytes(text), null, Ct);
        }

        var initializer = new IndexingSessionInitializer(new InMemoryNoteIndexFactory(), fixture.Analyzer, fixture.Events, fixture.Clock.Time, NullLoggerFactory.Instance);
        await initializer.InitializeAsync(fixture.Session, Ct);
        await fixture.Session.Get<VaultIndexer>().ScanCompletion;
        return fixture;
    }

    public async Task<string> ReadAsync(string path) => (await FileSystem.ReadAllTextAsync(P(path), Ct)).Value;

    public Task<bool> ExistsAsync(string path) => FileSystem.ExistsAsync(P(path), Ct);

    /// <summary>Lets pending events reach the indexer and flushes its debounce.</summary>
    public async Task SettleAsync()
    {
        await Events.WaitForIdleAsync(Ct);
        await Session.Get<VaultIndexer>().FlushAsync(Ct);
        await Events.WaitForIdleAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await Sessions.DisposeAsync();
        await Events.DisposeAsync();
    }
}
