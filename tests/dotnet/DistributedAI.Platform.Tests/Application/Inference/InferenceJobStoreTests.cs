using DistributedAI.Platform;
using Xunit;

namespace DistributedAI.Platform.Tests;

public sealed class InferenceJobStoreTests
{
    [Theory]
    [InlineData(InferenceJobStates.Completed)]
    [InlineData(InferenceJobStates.Failed)]
    [InlineData(InferenceJobStates.Cancelled)]
    [InlineData(InferenceJobStates.TimedOut)]
    public void Job_follows_accepted_running_terminal_lifecycle(string terminalState)
    {
        var store = new InferenceJobStore(TimeProvider.System);
        var job = store.CreateAccepted(Plan());

        Assert.Equal(InferenceJobStates.Accepted, job.State);
        Assert.True(store.TryStart(job.Id));
        Assert.False(store.TryStart(job.Id));
        Assert.True(store.TryFinish(job.Id, terminalState));
        Assert.False(store.TryFinish(job.Id, InferenceJobStates.Completed));

        var completed = store.Get(job.Id);
        Assert.NotNull(completed);
        Assert.Equal(terminalState, completed.State);
        Assert.NotNull(completed.StartedAtUtc);
        Assert.NotNull(completed.FinishedAtUtc);
    }

    [Fact]
    public void Completed_jobs_are_evicted_before_active_jobs()
    {
        var store = new InferenceJobStore(TimeProvider.System);
        var jobs = Enumerable.Range(0, 256).Select(_ => store.CreateAccepted(Plan())).ToArray();
        store.TryStart(jobs[0].Id);
        store.TryFinish(jobs[0].Id, InferenceJobStates.Completed);
        var newest = store.CreateAccepted(Plan());

        Assert.NotNull(store.Get(jobs[1].Id));
        Assert.Null(store.Get(jobs[0].Id));
        Assert.NotNull(store.Get(newest.Id));
    }

    [Fact]
    public void Registry_rejects_admission_when_all_256_retained_jobs_are_active()
    {
        var store = new InferenceJobStore(TimeProvider.System);
        var jobs = Enumerable.Range(0, 256).Select(_ => store.CreateAccepted(Plan())).ToArray();
        foreach (var job in jobs) store.TryStart(job.Id);

        Assert.Throws<InferenceJobCapacityException>(() => store.CreateAccepted(Plan()));
        Assert.All(jobs, job => Assert.NotNull(store.Get(job.Id)));
    }

    private static InferencePlacementPlan Plan() => new(
        InferenceWorkloadModes.SingleModelDistributed,
        "qwen-fast",
        InferenceWorkloadScheduler.RpcBackend,
        ["adb-a", "adb-b", "adb-c"]);
}
