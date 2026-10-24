using System.Diagnostics;
using Basalt.Application.Links;
using Basalt.Application.Notes;
using Basalt.Application.Tests.TestSupport;
using Basalt.Domain.Notes;

namespace Basalt.Application.Tests.Performance;

/// <summary>
/// NFR-007 and NFR-008 over the in-memory vault (validated by the same contracts as the physical one): backlink updates
/// after a save and link rewriting when renaming a note with 500 backlinks.
/// </summary>
public sealed class EditingPerformanceTests
{
    private const int BacklinkCount = 500;
    private static readonly VaultPath Target = VaultPath.Create("Notes/Target.md").Value;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Performance")]
    [Trait("Spec", "NFR-007")]
    public async Task Backlinks_AreUpdatedWithin500msAfterSaving()
    {
        await using var vault = await CreateVaultAsync();
        var save = new SaveNoteHandler(vault.Sessions, vault.Events);
        var backlinks = new GetBacklinksHandler(vault.Sessions, vault.Analyzer);
        (await save.HandleAsync(new SaveNote(VaultFixture.Id, Target, "# Target\n\nedited", null), Ct))
            .IsSuccess.Should().BeTrue();

        var watch = Stopwatch.StartNew();
        await vault.SettleAsync();
        var result = await backlinks.HandleAsync(new GetBacklinks(VaultFixture.Id, Target, IncludeUnlinked: false), Ct);
        watch.Stop();

        result.Value.Linked.Should().HaveCount(BacklinkCount);
        TestContext.Current.SendDiagnosticMessage($"Backlinks after save: {watch.Elapsed.TotalMilliseconds:F1} ms");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    [Trait("Category", "Performance")]
    [Trait("Spec", "NFR-008")]
    public async Task RenamingANoteWith500Backlinks_FinishesWithin3s()
    {
        await using var vault = await CreateVaultAsync();
        var rename = new RenameEntryHandler(vault.Sessions, new EntryMover(vault.Analyzer, vault.Events));

        var watch = Stopwatch.StartNew();
        var renamed = await rename.HandleAsync(new RenameEntry(VaultFixture.Id, Target, "Renamed"), Ct);
        watch.Stop();

        renamed.IsSuccess.Should().BeTrue();
        renamed.Value.Name.Should().Be("Renamed.md");
        for (var i = 0; i < BacklinkCount; i++)
        {
            (await vault.ReadAsync($"Notes/Linker {i:D3}.md")).Should().Contain("[[Renamed]]");
        }

        TestContext.Current.SendDiagnosticMessage($"Rename with {BacklinkCount} backlinks: {watch.Elapsed.TotalMilliseconds:F0} ms");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    private static async Task<VaultFixture> CreateVaultAsync()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Notes/Target.md"] = "# Target",
        };
        for (var i = 0; i < BacklinkCount; i++)
        {
            files[$"Notes/Linker {i:D3}.md"] = $"# Linker {i:D3}\n\nSee [[Target]].";
        }

        return await VaultFixture.CreateAsync(files);
    }
}
