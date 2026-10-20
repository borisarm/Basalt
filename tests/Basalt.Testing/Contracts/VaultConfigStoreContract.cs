using Basalt.Application.Bookmarks;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Workspace;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;

namespace Basalt.Testing.Contracts;

/// <summary>Behaviour of <see cref="IVaultConfigStore"/> over an in-memory vault.</summary>
public abstract class VaultConfigStoreContract : ContractTest<IVaultConfigStore>
{
    protected InMemoryVaultFileSystem Vault { get; } = new();

    protected abstract Task CorruptAsync();

    /// <summary>Leaves the stored bookmarks unreadable (e.g. sync conflict markers).</summary>
    protected abstract Task CorruptBookmarksAsync();

    protected static readonly Guid DeviceA = new("0000000d-0000-0000-0000-00000000000a");
    protected static readonly Guid DeviceB = new("0000000d-0000-0000-0000-00000000000b");

    protected static BookmarksDocument SampleBookmarks { get; } = new(
    [
        Bookmark.ForFile("Proyectos/A.md"),
        Bookmark.ForFile("B.md", "#Tema", "Tema de B"),
        Bookmark.ForGroup("Trabajo", Bookmark.ForFolder("Clientes"), Bookmark.ForSearch("tag:#urgente", "Urgente")),
    ]);

    protected static WorkspaceState SampleWorkspace { get; } = new(
        [new WorkspacePane([new WorkspaceTab("note", "A.md", Pinned: true, ViewState: "{\"scroll\":12}"), new WorkspaceTab("graph")], ActiveTab: 1, Size: 0.6), new WorkspacePane([], Size: 0.4)],
        ActivePane: 1,
        Orientation: SplitOrientation.Horizontal,
        Left: new WorkspaceSidebar(true, 300, "files"),
        Right: new WorkspaceSidebar(false));

    protected static VaultConfig Sample { get; } = new(
        new VaultId(new Guid("3f2c8a1e-0000-4000-8000-000000000001")),
        VaultSettings.Default with { AttachmentFolder = "Adjuntos", LinkFormat = LinkFormat.Markdown, DailyNotes = new DailyNotesSettings("Diario", "YYYY/YYYY-MM-DD") });

    [Fact]
    public async Task Missing_IsReported()
    {
        var load = await Sut.LoadVaultConfigAsync(Vault, Ct);

        load.Status.Should().Be(DocumentLoadStatus.Missing);
        load.Value.Should().BeNull();
    }

    [Fact]
    [Trait("Spec", "F-001.1")]
    public async Task Save_ThenLoad_RoundTrips()
    {
        (await Sut.SaveVaultConfigAsync(Vault, Sample, Ct)).IsSuccess.Should().BeTrue();

        var load = await Sut.LoadVaultConfigAsync(Vault, Ct);

        load.Status.Should().Be(DocumentLoadStatus.Loaded);
        load.Value.Should().BeEquivalentTo(Sample);
    }

    [Fact]
    public async Task Corrupt_IsInvalidAndNotOverwritten()
    {
        await Sut.SaveVaultConfigAsync(Vault, Sample, Ct);
        await CorruptAsync();

        var load = await Sut.LoadVaultConfigAsync(Vault, Ct);

        load.Status.Should().Be(DocumentLoadStatus.Invalid);
        load.Value.Should().BeNull();
    }

    [Fact]
    public async Task Bookmarks_MissingAreEmpty()
    {
        var load = await Sut.LoadBookmarksAsync(Vault, Ct);

        load.Status.Should().Be(DocumentLoadStatus.Missing);
        load.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Bookmarks_RoundTrip()
    {
        (await Sut.SaveBookmarksAsync(Vault, SampleBookmarks, Ct)).IsSuccess.Should().BeTrue();

        var load = await Sut.LoadBookmarksAsync(Vault, Ct);

        load.Status.Should().Be(DocumentLoadStatus.Loaded);
        load.Value.Should().BeEquivalentTo(SampleBookmarks);
    }

    [Fact]
    [Trait("Spec", "PERS-1.1")]
    public async Task Bookmarks_Corrupt_AreInvalidAndEmpty_UntilSavedAgain()
    {
        await Sut.SaveBookmarksAsync(Vault, SampleBookmarks, Ct);
        await CorruptBookmarksAsync();

        var load = await Sut.LoadBookmarksAsync(Vault, Ct);
        load.Status.Should().Be(DocumentLoadStatus.Invalid);
        load.Value.Items.Should().BeEmpty();

        await Sut.SaveBookmarksAsync(Vault, BookmarksDocument.Empty with { Items = [Bookmark.ForFile("C.md")] }, Ct);
        (await Sut.LoadBookmarksAsync(Vault, Ct)).Value.Items.Should().ContainSingle();
    }

    [Fact]
    [Trait("Spec", "F-013")]
    public async Task Workspace_IsPerDevice_AndRoundTrips()
    {
        (await Sut.LoadWorkspaceAsync(Vault, DeviceA, Ct)).Status.Should().Be(DocumentLoadStatus.Missing);

        await Sut.SaveWorkspaceAsync(Vault, DeviceA, SampleWorkspace, Ct);

        (await Sut.LoadWorkspaceAsync(Vault, DeviceA, Ct)).Value.Should().BeEquivalentTo(SampleWorkspace);
        (await Sut.LoadWorkspaceAsync(Vault, DeviceB, Ct)).Value.Should().BeNull();
    }
}
