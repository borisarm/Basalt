using System.Text;
using Basalt.Application.Ports.Security;

namespace Basalt.Testing.Contracts;

/// <summary>Behaviour of <see cref="ISecretStore"/> (spec 03 §1.5).</summary>
public abstract class SecretStoreContract : ContractTest<ISecretStore>
{
    [Fact]
    [Trait("Spec", "SECRET-STORE")]
    public async Task MissingKey_ReturnsNull()
    {
        var value = await Sut.GetAsync("basalt/test/missing", Ct);

        value.IsSuccess.Should().BeTrue();
        value.Value.Should().BeNull();
    }

    [Fact]
    [Trait("Spec", "SECRET-STORE")]
    public async Task SetThenGet_RoundTripsBytes()
    {
        var secret = Encoding.UTF8.GetBytes("token-value");

        (await Sut.SetAsync("basalt/test/token", secret, Ct)).IsSuccess.Should().BeTrue();

        (await Sut.GetAsync("basalt/test/token", Ct)).Value.Should().Equal(secret);
    }

    [Fact]
    [Trait("Spec", "SECRET-STORE")]
    public async Task Set_Overwrites()
    {
        await Sut.SetAsync("basalt/test/token", Encoding.UTF8.GetBytes("one"), Ct);
        await Sut.SetAsync("basalt/test/token", Encoding.UTF8.GetBytes("two"), Ct);

        (await Sut.GetAsync("basalt/test/token", Ct)).Value.Should().Equal(Encoding.UTF8.GetBytes("two"));
    }

    [Fact]
    [Trait("Spec", "SECRET-STORE")]
    public async Task Delete_RemovesTheValue_AndIsIdempotent()
    {
        await Sut.SetAsync("basalt/test/token", Encoding.UTF8.GetBytes("one"), Ct);

        (await Sut.DeleteAsync("basalt/test/token", Ct)).IsSuccess.Should().BeTrue();
        (await Sut.GetAsync("basalt/test/token", Ct)).Value.Should().BeNull();
        (await Sut.DeleteAsync("basalt/test/token", Ct)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    [Trait("Spec", "SECRET-STORE")]
    public async Task EmptySecret_IsAllowedAndDistinctFromMissing()
    {
        await Sut.SetAsync("basalt/test/empty", ReadOnlyMemory<byte>.Empty, Ct);

        var empty = await Sut.GetAsync("basalt/test/empty", Ct);

        empty.Value.Should().NotBeNull();
        empty.Value.Should().BeEmpty();
    }
}
