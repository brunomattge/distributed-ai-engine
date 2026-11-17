namespace DistributedAI.Platform;

public static class InferenceJobStates
{
    public const string Accepted = "accepted";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed_out";
}

public sealed record InferenceJob(
    Guid Id,
    string State,
    string WorkloadMode,
    string ModelId,
    string Backend,
    IReadOnlyList<string> SelectedNodeIds,
    DateTimeOffset AcceptedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? FailureCode);

public sealed class InferenceJobCapacityException()
    : Exception("The local inference job registry is full of active requests.");

public interface IInferenceJobStore
{
    InferenceJob CreateAccepted(InferencePlacementPlan plan);
    InferenceJob? Get(Guid id);
    bool TryStart(Guid id);
    bool TryFinish(Guid id, string terminalState, string? failureCode = null);
}

/// <summary>
/// Keeps a bounded, process-local record of recent requests for debugging.
/// It is not durable job storage and does not support execution recovery.
/// </summary>
public sealed class InferenceJobStore(TimeProvider clock) : IInferenceJobStore
{
    private const int RetainedJobLimit = 256;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, InferenceJob> _jobs = [];
    private readonly Queue<Guid> _order = [];

    public InferenceJob CreateAccepted(InferencePlacementPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var job = new InferenceJob(
            Guid.NewGuid(),
            InferenceJobStates.Accepted,
            plan.WorkloadMode,
            plan.ModelId,
            plan.Backend,
            plan.SelectedNodeIds.ToArray(),
            clock.GetUtcNow(),
            null,
            null,
            null);

        lock (_gate)
        {
            while (_jobs.Count >= RetainedJobLimit && RemoveOldestTerminal()) { }
            if (_jobs.Count >= RetainedJobLimit)
            {
                throw new InferenceJobCapacityException();
            }

            _jobs.Add(job.Id, job);
            _order.Enqueue(job.Id);
        }

        return job;
    }

    public InferenceJob? Get(Guid id)
    {
        lock (_gate)
        {
            return _jobs.GetValueOrDefault(id);
        }
    }

    public bool TryStart(Guid id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var current) || current.State != InferenceJobStates.Accepted)
            {
                return false;
            }

            _jobs[id] = current with
            {
                State = InferenceJobStates.Running,
                StartedAtUtc = clock.GetUtcNow(),
            };
            return true;
        }
    }

    public bool TryFinish(Guid id, string terminalState, string? failureCode = null)
    {
        if (terminalState is not (InferenceJobStates.Completed or InferenceJobStates.Failed or
            InferenceJobStates.Cancelled or InferenceJobStates.TimedOut))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalState), "A job must finish in a terminal state.");
        }

        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var current) || current.State != InferenceJobStates.Running)
            {
                return false;
            }

            _jobs[id] = current with
            {
                State = terminalState,
                FinishedAtUtc = clock.GetUtcNow(),
                FailureCode = failureCode,
            };
            return true;
        }
    }

    private bool RemoveOldestTerminal()
    {
        var retained = new Queue<Guid>();
        var removed = false;
        while (_order.TryDequeue(out var id))
        {
            if (!removed && _jobs.TryGetValue(id, out var job) && IsTerminal(job.State))
            {
                _jobs.Remove(id);
                removed = true;
            }
            else if (_jobs.ContainsKey(id))
            {
                retained.Enqueue(id);
            }
        }

        while (retained.TryDequeue(out var id)) _order.Enqueue(id);
        return removed;
    }

    private static bool IsTerminal(string state) =>
        state is InferenceJobStates.Completed or InferenceJobStates.Failed or
            InferenceJobStates.Cancelled or InferenceJobStates.TimedOut;
}
