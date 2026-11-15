using System.Text.Json;
using DistributedAI.Contracts;

namespace DistributedAI.Platform;

public interface IInferenceReadiness
{
    Task<InferenceStatus> GetAsync(CancellationToken cancellationToken);
    void ReportSessionFailure();
}

/// <summary>
/// Combines the coordinator's model health with the fixed three-worker Android
/// requirement used by the current llama.cpp RPC experiment.
/// </summary>
public sealed class InferenceReadinessService(NodeRegistry nodes, IPythonAgentClient agent) : IInferenceReadiness
{
    public const int RequiredWorkerCount = 3;
    private readonly object _sessionLock = new();
    private bool _sessionFailed;
    private long _failedAtGeneration;
    private long _lastObservedGeneration;

    public void ReportSessionFailure()
    {
        lock (_sessionLock)
        {
            if (_sessionFailed) return;
            _sessionFailed = true;
            _failedAtGeneration = _lastObservedGeneration;
        }
    }

    public async Task<InferenceStatus> GetAsync(CancellationToken cancellationToken)
    {
        var coordinatorState = "unavailable";
        var modelId = "none";
        var modelName = "No model loaded";
        var supervisorState = "ready";
        var supervisorMessage = (string?)null;
        var supervisorGeneration = 0L;
        try
        {
            var health = await agent.GetHealthAsync(cancellationToken);
            coordinatorState = ReadString(health, "state") ?? "unavailable";
            modelId = ReadString(health, "model_id") ?? modelId;
            modelName = ReadString(health, "model_name") ?? modelName;
            supervisorState = ReadString(health, "supervisor_state") ?? supervisorState;
            supervisorMessage = ReadString(health, "supervisor_message");
            supervisorGeneration = ReadInt64(health, "supervisor_generation") ?? supervisorGeneration;
        }
        catch (PythonAgentUnavailableException)
        {
            // Keep coordinator health unavailable; the node observations below remain useful.
        }

        var snapshots = nodes.GetAll();
        var activeRpcWorkers = snapshots.Count(node =>
            node.NodeId.StartsWith("adb-", StringComparison.Ordinal) &&
            node.Health == NodeHealth.Online && node.RpcWorkerProcessState == RpcWorkerProcessState.Running);
        var allRequiredRpcWorkersActive = snapshots.Count == RequiredWorkerCount && activeRpcWorkers == RequiredWorkerCount;

        lock (_sessionLock)
        {
            _lastObservedGeneration = Math.Max(_lastObservedGeneration, supervisorGeneration);
            if (_sessionFailed && supervisorState == "ready" &&
                supervisorGeneration > _failedAtGeneration && coordinatorState == "ready" && allRequiredRpcWorkersActive)
            {
                _sessionFailed = false;
            }

            if (_sessionFailed || supervisorState is "degraded" or "restarting" or "waiting_for_devices" or "failed")
            {
                var message = supervisorMessage ?? (supervisorState == "failed"
                    ? "Automatic recovery failed. Check the Terminal and restart the session manually."
                    : "The RPC session was interrupted. The supervisor is recovering the cluster; wait before sending another message.");
                return new InferenceStatus(false, "degraded", coordinatorState, activeRpcWorkers, RequiredWorkerCount, modelId, modelName, message);
            }
        }

        if (coordinatorState != "ready")
        {
            var message = coordinatorState == "loading"
                ? "The coordinator is still loading the model."
                : "The coordinator has not confirmed that the model is loaded.";
            return new InferenceStatus(false, coordinatorState, coordinatorState, activeRpcWorkers, RequiredWorkerCount, modelId, modelName, message);
        }

        if (!allRequiredRpcWorkersActive)
        {
            var message = $"Distributed chat requires {RequiredWorkerCount} active RPC processes on online devices; active: {activeRpcWorkers}/{RequiredWorkerCount}.";
            return new InferenceStatus(false, "cluster_incomplete", coordinatorState, activeRpcWorkers, RequiredWorkerCount, modelId, modelName, message);
        }

        return new InferenceStatus(true, "ready", coordinatorState, activeRpcWorkers, RequiredWorkerCount, modelId, modelName, null);
    }

    private static string? ReadString(JsonElement json, string propertyName) =>
        json.ValueKind == JsonValueKind.Object &&
        json.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadInt64(JsonElement json, string propertyName) =>
        json.ValueKind == JsonValueKind.Object &&
        json.TryGetProperty(propertyName, out var value) &&
        value.TryGetInt64(out var number)
            ? number
            : null;
}
