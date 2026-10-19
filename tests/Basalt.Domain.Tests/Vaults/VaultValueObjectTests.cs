using Basalt.Domain.Common;
using Basalt.Domain.Vaults;

namespace Basalt.Domain.Tests.Vaults;

public sealed class VaultValueObjectTests
{
    [Theory]
    [InlineData("Notas")]
    [InlineData("Personal Vault")]
    [InlineData("a")]
    [InlineData("Vault (2)")]
    [InlineData("Trabajo-2026_Q4")]
    [InlineData("日本語")]
    [Trait("Spec", "F-001.1")]
    public void VaultName_Valid(string name)
    {
        VaultName.Create(name).Value.ToString().Should().Be(name);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" Notas")]
    [InlineData("Notas ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [Trait("Spec", "F-001.2")]
    public void VaultName_Invalid(string name)
    {
        VaultName.Create(name).Error.Code.Should().Be(DomainErrors.VaultInvalidName);
    }

    [Fact]
    public void VaultName_LengthLimitIs100()
    {
        VaultName.Create(new string('a', 100)).IsSuccess.Should().BeTrue();
        VaultName.Create(new string('a', 101)).Error.Code.Should().Be(DomainErrors.VaultInvalidName);
        FluentActions.Invoking(() => VaultName.Create(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void VaultName_ErrorMessage_IsDescriptive()
    {
        VaultName.Create("").Error.Message.Should().Be("Vault names must have 1-100 characters, no surrounding spaces and no / \\ : * ? \" < > |.");
    }

    [Fact]
    public void VaultId_WrapsGuidAndParses()
    {
        var guid = new Guid("3f2c8a1e-0000-4000-8000-000000000001");
        var id = new VaultId(guid);

        id.Value.Should().Be(guid);
        id.ToString().Should().Be("3f2c8a1e-0000-4000-8000-000000000001");
        VaultId.Parse("3f2c8a1e-0000-4000-8000-000000000001").Value.Should().Be(id);
        VaultId.Parse("not-a-guid").Error.Code.Should().Be(DomainErrors.VaultInvalidId);
        VaultId.Parse(Guid.Empty.ToString()).Error.Code.Should().Be(DomainErrors.VaultInvalidId);
        FluentActions.Invoking(() => VaultId.Parse(null!)).Should().Throw<ArgumentNullException>();
        VaultId.Parse("x").Error.Message.Should().Be("Vault ids are non-empty GUIDs.");
    }

    [Fact]
    public void VaultSettings_Defaults()
    {
        var settings = new VaultSettings();
        VaultSettings.Default.Should().Be(settings);

        settings.LinkFormat.Should().Be(LinkFormat.Wikilink);
        settings.LinkPathStyle.Should().Be(LinkPathStyle.Shortest);
        settings.AutoUpdateLinks.Should().BeTrue();
        settings.AttachmentFolder.Should().BeEmpty();
        settings.NewNoteFolder.Should().BeEmpty();
        settings.DailyNotes.DateFormat.Should().Be("YYYY-MM-DD");
        settings.DailyNotes.Folder.Should().BeEmpty();
        settings.DailyNotes.TemplatePath.Should().BeNull();
        settings.TemplatesFolder.Should().BeEmpty();
        settings.VisibleExtensions.Should().BeEmpty();
    }
}
