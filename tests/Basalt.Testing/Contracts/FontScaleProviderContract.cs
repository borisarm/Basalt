using Basalt.Application.Ports.Platform;

namespace Basalt.Testing.Contracts;

/// <summary>Behaviour of <see cref="IFontScaleProvider"/> (spec 07 §4).</summary>
public abstract class FontScaleProviderContract : ContractTest<IFontScaleProvider>
{
    [Fact]
    public void Scale_IsPositive()
    {
        Sut.Scale.Should().BePositive();
    }
}
