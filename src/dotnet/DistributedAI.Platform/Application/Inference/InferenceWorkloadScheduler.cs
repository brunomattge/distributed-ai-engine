using DistributedAI.Contracts;

namespace DistributedAI.Platform;

public static class InferenceWorkloadModes
{
    public const string SingleModelDistributed = "single_model_distributed";
    public const string IndependentReplicas = "independent_replicas";
}

public sealed record InferencePlacementRequirement(
    string ModelId,
    string Backend,
    int WorkerCount);

public sealed record InferenceWorkerCapability(
    string NodeId,
    bool IsOnline,
    bool RpcWorkerRunning,
    IReadOnlySet<string> SupportedModels,
    IReadOnlySet<string> SupportedBackends);

public sealed record InferencePlacementPlan(
    string WorkloadMode,
    string ModelId,
    string Backend,
    IReadOnlyList<string> SelectedNodeIds);

public sealed class InferenceWorkloadScheduler
{
    public const string RpcBackend = "llama.cpp-rpc";

    public InferencePlacementPlan? PlanSingleModel(
        InferencePlacementRequirement requirement,
        IEnumerable<InferenceWorkerCapability> workers)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(workers);
        if (string.IsNullOrWhiteSpace(requirement.ModelId) ||
            string.IsNullOrWhiteSpace(requirement.Backend) ||
            requirement.WorkerCount < 1)
        {
            return null;
        }

        var selected = workers
            .Where(worker => worker.IsOnline &&
                worker.RpcWorkerRunning &&
                worker.SupportedModels.Contains(requirement.ModelId) &&
                worker.SupportedBackends.Contains(requirement.Backend))
            .Select(worker => worker.NodeId)
            .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(nodeId => nodeId, StringComparer.Ordinal)
            .Take(requirement.WorkerCount)
            .ToArray();

        if (selected.Length != requirement.WorkerCount)
        {
            return null;
        }

        return new InferencePlacementPlan(
            InferenceWorkloadModes.SingleModelDistributed,
            requirement.ModelId,
            requirement.Backend,
            selected);
    }

    public static IReadOnlyList<InferenceWorkerCapability> FromCurrentCluster(
        IEnumerable<NodeSnapshot> nodes,
        string loadedModelId)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentException.ThrowIfNullOrWhiteSpace(loadedModelId);

        return nodes
            .Where(node => node.NodeId.StartsWith("adb-", StringComparison.Ordinal))
            .Select(node => new InferenceWorkerCapability(
                node.NodeId,
                node.Health == NodeHealth.Online,
                node.RpcWorkerProcessState == RpcWorkerProcessState.Running,
                new HashSet<string>([loadedModelId], StringComparer.Ordinal),
                new HashSet<string>([RpcBackend], StringComparer.Ordinal)))
            .ToArray();
    }
}
