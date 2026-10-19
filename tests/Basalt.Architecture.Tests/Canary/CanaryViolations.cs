// Deliberate rule violations used to prove that each architecture rule can fail (T0.2 acceptance).
#pragma warning disable CA1822, CA5394, IDE0051, CA1823, IDE0161, CA1050
namespace Basalt.Architecture.Tests.Canary.Sync
{
    public sealed class UsesClockAndRandomness
    {
        public DateTime Now() => DateTime.Now;

        public Guid NewId() => Guid.NewGuid();

        public int Roll() => new Random(1).Next();

        public int Blocking(Task<int> task) => task.Result;

        public Graph.GraphThing DependsOnForbiddenModule() => new();
    }
}

namespace Basalt.Architecture.Tests.Canary.Graph
{
    public sealed class GraphThing;
}

namespace Basalt.Architecture.Tests.Canary.Ports
{
    public interface ILeakyPort
    {
        Task<System.Net.Http.HttpResponseMessage> SendAsync(CancellationToken ct);
    }
}

namespace Basalt.Architecture.Tests.Canary.Handlers
{
    public interface ICommandHandler<in TCommand, TResult>;

    public class UnsealedHandler : ICommandHandler<string, int>;

    public sealed class AsyncVoidOwner
    {
        public async void Fire() => await Task.Yield();
    }
}
#pragma warning restore CA1822, CA5394, IDE0051, CA1823, IDE0161, CA1050
