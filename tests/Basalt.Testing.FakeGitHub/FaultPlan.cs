namespace Basalt.Testing.FakeGitHub;

/// <summary>Programmable failures for the fake server (spec 06 §14).</summary>
public sealed class FaultPlan
{
    private readonly List<FaultRule> _rules = [];
    private readonly object _gate = new();

    public void FailNext(
        string method,
        string pathContains,
        int status = 500,
        TimeSpan? delay = null,
        TimeSpan? retryAfter = null,
        int times = 1)
        => Add(new FaultRule(method, pathContains, 1, times, FaultKind.Status, status, delay, retryAfter));

    public void FailNth(
        string method,
        string pathContains,
        int nth,
        int status = 500,
        TimeSpan? delay = null,
        TimeSpan? retryAfter = null,
        int times = 1)
        => Add(new FaultRule(method, pathContains, nth, times, FaultKind.Status, status, delay, retryAfter));

    public void DropConnectionNext(string method, string pathContains, int times = 1)
        => Add(new FaultRule(method, pathContains, 1, times, FaultKind.DropConnection, 0, null, null));

    public void TruncateNext(string method, string pathContains, int times = 1)
        => Add(new FaultRule(method, pathContains, 1, times, FaultKind.Truncate, 0, null, null));

    public void Clear()
    {
        lock (_gate)
        {
            _rules.Clear();
        }
    }

    internal Fault? Match(string method, string path)
    {
        lock (_gate)
        {
            foreach (var rule in _rules)
            {
                if (!rule.Method.Equals(method, StringComparison.OrdinalIgnoreCase)
                    || !path.Contains(rule.PathContains, StringComparison.Ordinal))
                {
                    continue;
                }

                rule.Seen++;
                if (rule.Seen < rule.Nth)
                {
                    continue;
                }

                rule.Remaining--;
                if (rule.Remaining <= 0)
                {
                    _rules.Remove(rule);
                }

                return new Fault(rule.Kind, rule.Status, rule.Delay, rule.RetryAfter);
            }

            return null;
        }
    }

    private void Add(FaultRule rule)
    {
        lock (_gate)
        {
            _rules.Add(rule);
        }
    }

    private sealed class FaultRule(
        string method,
        string pathContains,
        int nth,
        int remaining,
        FaultKind kind,
        int status,
        TimeSpan? delay,
        TimeSpan? retryAfter)
    {
        public string Method { get; } = method;

        public string PathContains { get; } = pathContains;

        public int Nth { get; } = nth;

        public int Remaining { get; set; } = remaining;

        public int Seen { get; set; }

        public FaultKind Kind { get; } = kind;

        public int Status { get; } = status;

        public TimeSpan? Delay { get; } = delay;

        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}

internal enum FaultKind
{
    Status,
    DropConnection,
    Truncate,
}

internal sealed record Fault(FaultKind Kind, int Status, TimeSpan? Delay, TimeSpan? RetryAfter);
