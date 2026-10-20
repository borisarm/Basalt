using Basalt.Application.Ports.Remote;
using Basalt.Domain.Accounts;

namespace Basalt.Testing.Contracts;

/// <summary>Behaviour of <see cref="IRepositoryAdministration"/> (spec 03 §1.4, 06 §10).</summary>
public abstract class RepositoryAdministrationContract : ContractTest<IRepositoryAdministration>
{
    private static readonly GitHubAccountId Account = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    [Trait("Spec", "REPO-ADMIN")]
    public async Task List_WithoutRepositories_IsEmpty()
    {
        var repositories = await Sut.ListAccessibleRepositoriesAsync(Account, Ct);

        repositories.IsSuccess.Should().BeTrue();
        repositories.Value.Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "REPO-ADMIN")]
    public async Task Create_ThenGetAndList_ExposeTheNewRepository()
    {
        var created = await Sut.CreateRepositoryAsync(Account, null, "vault", isPrivate: true, Ct);

        created.IsSuccess.Should().BeTrue();
        created.Value.Name.Should().Be("vault");
        created.Value.DefaultBranch.Should().NotBeNullOrEmpty();
        created.Value.IsPrivate.Should().BeTrue();

        var fetched = await Sut.GetRepositoryAsync(Account, created.Value.Owner, "vault", Ct);
        fetched.Value.Should().Be(created.Value);

        var listed = await Sut.ListAccessibleRepositoriesAsync(Account, Ct);
        listed.Value.Should().Contain(repository => repository.Name == "vault");
    }

    [Fact]
    [Trait("Spec", "REPO-ADMIN")]
    public async Task Create_Duplicate_ReturnsValidationFailed()
    {
        await Sut.CreateRepositoryAsync(Account, null, "vault", isPrivate: true, Ct);

        var duplicate = await Sut.CreateRepositoryAsync(Account, null, "vault", isPrivate: true, Ct);

        duplicate.Error.Code.Should().Be(RemoteErrors.ValidationFailed);
    }

    [Fact]
    [Trait("Spec", "REPO-ADMIN")]
    public async Task Get_Missing_ReturnsNotFound()
    {
        var missing = await Sut.GetRepositoryAsync(Account, "acme", "missing", Ct);

        missing.Error.Code.Should().Be(RemoteErrors.NotFound);
    }

    [Fact]
    [Trait("Spec", "REPO-ADMIN")]
    public async Task Create_HonoursVisibility()
    {
        var created = await Sut.CreateRepositoryAsync(Account, null, "public-vault", isPrivate: false, Ct);

        created.Value.IsPrivate.Should().BeFalse();
    }
}
