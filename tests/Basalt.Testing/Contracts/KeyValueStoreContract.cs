using Basalt.Application.Ports.Storage;

namespace Basalt.Testing.Contracts;

/// <summary>Behaviour of <see cref="IDocumentStore{T}"/> (spec 03 §3, 08 §1.1) shared by fake and JSON adapter.</summary>
public abstract class KeyValueStoreContract<T> : ContractTest<IDocumentStore<T>>
    where T : class
{
    /// <summary>The value returned when the document is missing or invalid.</summary>
    protected abstract T DefaultValue { get; }

    /// <summary>Two distinct non-default sample documents.</summary>
    protected abstract (T First, T Second) Samples { get; }

    /// <summary>Makes the stored document unreadable (garbage on disk, flag in memory).</summary>
    protected abstract Task CorruptAsync();

    [Fact]
    public async Task Load_Missing_ReturnsDefault()
    {
        var load = await Sut.LoadAsync(Ct);

        load.Status.Should().Be(DocumentLoadStatus.Missing);
        load.Value.Should().BeEquivalentTo(DefaultValue);
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTrips()
    {
        (await Sut.SaveAsync(Samples.First, Ct)).IsSuccess.Should().BeTrue();

        var load = await Sut.LoadAsync(Ct);

        load.Status.Should().Be(DocumentLoadStatus.Loaded);
        load.Value.Should().BeEquivalentTo(Samples.First);
    }

    [Fact]
    public async Task Save_Overwrites()
    {
        await Sut.SaveAsync(Samples.First, Ct);
        await Sut.SaveAsync(Samples.Second, Ct);

        (await Sut.LoadAsync(Ct)).Value.Should().BeEquivalentTo(Samples.Second);
    }

    [Fact]
    [Trait("Spec", "PERS-1.1")]
    public async Task Load_Corrupt_ReturnsDefaultAsInvalid_ThenSaveRecovers()
    {
        await Sut.SaveAsync(Samples.First, Ct);
        await CorruptAsync();

        var load = await Sut.LoadAsync(Ct);

        load.Status.Should().Be(DocumentLoadStatus.Invalid);
        load.Problem.Should().NotBeNullOrEmpty();
        load.Value.Should().BeEquivalentTo(DefaultValue);

        await Sut.SaveAsync(Samples.Second, Ct);
        (await Sut.LoadAsync(Ct)).Value.Should().BeEquivalentTo(Samples.Second);
    }

    [Fact]
    public async Task ConcurrentSaves_LeaveOneCompleteDocument()
    {
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Sut.SaveAsync(i % 2 == 0 ? Samples.First : Samples.Second, Ct)));

        var load = await Sut.LoadAsync(Ct);

        load.Status.Should().Be(DocumentLoadStatus.Loaded);
        load.Value.Should().Match<T>(v => Equivalent(v, Samples.First) || Equivalent(v, Samples.Second));
    }

    private static bool Equivalent(T actual, T expected)
    {
        try
        {
            actual.Should().BeEquivalentTo(expected);
            return true;
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("Exception", StringComparison.Ordinal))
        {
            return false;
        }
    }
}
