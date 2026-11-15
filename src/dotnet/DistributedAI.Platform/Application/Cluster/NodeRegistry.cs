using DistributedAI.Contracts;

namespace DistributedAI.Platform;

public enum RegistrationResult
{
    Created,
    Invalid,
    Conflict
}

public enum HeartbeatResult
{
    Accepted,
    Invalid,
    NotFound
}

public enum DisconnectResult
{
    Disconnected,
    NotFound
}

public sealed class NodeRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, NodeState> _nodes = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly TimeSpan _offlineAfter;

    public NodeRegistry(TimeProvider clock, TimeSpan offlineAfter)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (offlineAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(offlineAfter), "Offline timeout must be positive.");
        }

        _clock = clock;
        _offlineAfter = offlineAfter;
    }

    public RegistrationResult Register(RegisterNodeRequest? request, out NodeSnapshot? snapshot)
    {
        snapshot = null;
        if (request is null ||
            string.IsNullOrWhiteSpace(request.NodeId) || request.NodeId.Trim().Length > 80 ||
            string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100 ||
            string.IsNullOrWhiteSpace(request.HardwareModel) || request.HardwareModel.Trim().Length > 100)
        {
            return RegistrationResult.Invalid;
        }

        var id = request.NodeId.Trim();
        lock (_gate)
        {
            if (_nodes.ContainsKey(id))
            {
                return RegistrationResult.Conflict;
            }

            var state = new NodeState(
                id,
                request.Name.Trim(),
                request.HardwareModel.Trim(),
                _clock.GetUtcNow(),
                null,
                null,
                RpcWorkerProcessState.Unknown,
                false);
            _nodes.Add(id, state);
            snapshot = ToSnapshot(state, _clock.GetUtcNow());
            return RegistrationResult.Created;
        }
    }

    public HeartbeatResult Heartbeat(string? nodeId, HeartbeatRequest? request, out NodeSnapshot? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(nodeId) || request is null || !IsValid(request))
        {
            return HeartbeatResult.Invalid;
        }

        var id = nodeId.Trim();
        lock (_gate)
        {
            if (!_nodes.TryGetValue(id, out var current))
            {
                return HeartbeatResult.NotFound;
            }

            var updated = current with
            {
                LastHeartbeatAtUtc = _clock.GetUtcNow(),
                Metrics = request,
                RpcWorkerProcessState = request.RpcWorkerProcessState,
                IsDisconnected = false
            };
            _nodes[id] = updated;
            snapshot = ToSnapshot(updated, _clock.GetUtcNow());
            return HeartbeatResult.Accepted;
        }
    }

    public DisconnectResult MarkDisconnected(string? nodeId, out NodeSnapshot? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return DisconnectResult.NotFound;
        }

        var id = nodeId.Trim();
        lock (_gate)
        {
            if (!_nodes.TryGetValue(id, out var current))
            {
                return DisconnectResult.NotFound;
            }

            var disconnected = current with { IsDisconnected = true };
            _nodes[id] = disconnected;
            snapshot = ToSnapshot(disconnected, _clock.GetUtcNow());
            return DisconnectResult.Disconnected;
        }
    }

    public IReadOnlyList<NodeSnapshot> GetAll()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            return _nodes.Values
                .Select(node => ToSnapshot(node, now))
                .OrderBy(node => node.NodeId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private NodeSnapshot ToSnapshot(NodeState node, DateTimeOffset now)
    {
        var health = node.IsDisconnected
            ? NodeHealth.Offline
            : node.LastHeartbeatAtUtc switch
            {
                null => NodeHealth.AwaitingHeartbeat,
                var last when now - last <= _offlineAfter => NodeHealth.Online,
                _ => NodeHealth.Offline
            };

        return new NodeSnapshot(
            node.NodeId,
            node.Name,
            node.HardwareModel,
            node.RegisteredAtUtc,
            node.LastHeartbeatAtUtc,
            health,
            health == NodeHealth.Online ? node.RpcWorkerProcessState : RpcWorkerProcessState.Unknown,
            node.Metrics);
    }

    private static bool IsValid(HeartbeatRequest request) =>
        Enum.IsDefined(request.RpcWorkerProcessState) &&
        double.IsFinite(request.CpuPercent) && request.CpuPercent is >= 0 and <= 100 &&
        request.AvailableRamMiB >= 0 &&
        (request.TemperatureCelsius is null ||
            (double.IsFinite(request.TemperatureCelsius.Value) && request.TemperatureCelsius is >= 0 and <= 120)) &&
        double.IsFinite(request.LatencyMs) && request.LatencyMs is >= 0 and <= 300_000;

    private sealed record NodeState(
        string NodeId,
        string Name,
        string HardwareModel,
        DateTimeOffset RegisteredAtUtc,
        DateTimeOffset? LastHeartbeatAtUtc,
        HeartbeatRequest? Metrics,
        RpcWorkerProcessState RpcWorkerProcessState,
        bool IsDisconnected);
}
