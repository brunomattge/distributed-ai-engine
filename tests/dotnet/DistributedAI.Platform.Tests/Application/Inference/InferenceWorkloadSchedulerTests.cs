using DistributedAI.Platform;
using Xunit;

namespace DistributedAI.Platform.Tests;

public sealed class InferenceWorkloadSchedulerTests
{
    [Fact]
    public void PlanSingleModel_selects_a_deterministic_eligible_set()
    {
        var workers = new[]
        {
            Worker("adb-c", online: true, running: true),
            Worker("adb-offline", online: false, running: true),
            Worker("adb-wrong-backend", online: true, running: true, backend: "other"),
            Worker("adb-a", online: true, running: true),
            Worker("adb-stopped", online: true, running: false),
            Worker("adb-b", online: true, running: true),
        };

        var scheduler = new InferenceWorkloadScheduler();
        var plan = scheduler.PlanSingleModel(
            new InferencePlacementRequirement("qwen-fast", InferenceWorkloadScheduler.RpcBackend, 3),
            workers);

        Assert.NotNull(plan);
        Assert.Equal(InferenceWorkloadModes.SingleModelDistributed, plan.WorkloadMode);
        Assert.Equal("qwen-fast", plan.ModelId);
        Assert.Equal(InferenceWorkloadScheduler.RpcBackend, plan.Backend);
        Assert.Equal(["adb-a", "adb-b", "adb-c"], plan.SelectedNodeIds);
    }

    [Fact]
    public void PlanSingleModel_rejects_nodes_without_the_requested_model()
    {
        var plan = new InferenceWorkloadScheduler().PlanSingleModel(
            new InferencePlacementRequirement("qwen-large", InferenceWorkloadScheduler.RpcBackend, 1),
            [Worker("adb-a", online: true, running: true)]);

        Assert.Null(plan);
    }

    private static InferenceWorkerCapability Worker(
        string nodeId,
        bool online,
        bool running,
        string model = "qwen-fast",
        string backend = InferenceWorkloadScheduler.RpcBackend) =>
        new(
            nodeId,
            online,
            running,
            new HashSet<string>([model], StringComparer.Ordinal),
            new HashSet<string>([backend], StringComparer.Ordinal));
}
