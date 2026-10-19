namespace Basalt.Testing.Contracts;

/// <summary>
/// Base for port contract tests (spec 09 §4). A contract is an abstract class deriving from this type with the
/// behavioural tests of a port; the fake (in <c>Basalt.Testing</c>) and each real adapter inherit it in their test
/// projects and only implement <see cref="CreateSutAsync"/>.
/// </summary>
public abstract class ContractTest<TSut> : IAsyncLifetime
    where TSut : class
{
    private TSut? _sut;

    protected TSut Sut => _sut ?? throw new InvalidOperationException("The SUT has not been created yet.");

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract Task<TSut> CreateSutAsync();

    public virtual async ValueTask InitializeAsync() => _sut = await CreateSutAsync();

    public virtual async ValueTask DisposeAsync()
    {
        switch (_sut)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }

        GC.SuppressFinalize(this);
    }
}
