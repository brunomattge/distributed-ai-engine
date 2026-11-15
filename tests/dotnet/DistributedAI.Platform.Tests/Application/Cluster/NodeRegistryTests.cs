using DistributedAI.Contracts;
using DistributedAI.Platform;
using Xunit;

namespace DistributedAI.Platform.Tests;

public sealed class NodeRegistryTests
{
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(6);

    [Fact]
    public void Register_creates_node_awaiting_first_heartbeat()
    {
        var clock = new ManualTimeProvider();
        var registry = new NodeRegistry(clock, OfflineAfter);

        var result = registry.Register(Node("a07-1"), out var node);

        Assert.Equal(RegistrationResult.Created, result);
        Assert.NotNull(node);
        Assert.Equal(NodeHealth.AwaitingHeartbeat, node.Health);
        Assert.Null(node.LastHeartbeatAtUtc);
    }

    [Fact]
    public void Register_rejects_duplicate_node_id()
    {
        var registry = NewRegistry();
        registry.Register(Node("a07-1"), out _);

        var result = registry.Register(Node("a07-1"), out var node);

        Assert.Equal(RegistrationResult.Conflict, result);
        Assert.Null(node);
        Assert.Single(registry.GetAll());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_rejects_missing_node_id(string? nodeId)
    {
        var registry = NewRegistry();

        var result = registry.Register(Node(nodeId), out var node);

        Assert.Equal(RegistrationResult.Invalid, result);
        Assert.Null(node);
    }

    [Fact]
    public void Heartbeat_updates_metrics_and_marks_node_online()
    {
        var clock = new ManualTimeProvider();
        var registry = new NodeRegistry(clock, OfflineAfter);
        registry.Register(Node("a07-1"), out _);
        clock.Advance(TimeSpan.FromSeconds(2));

        var result = registry.Heartbeat("a07-1", new HeartbeatRequest(42.5, 812, 37.2, 5.4), out var node);

        Assert.Equal(HeartbeatResult.Accepted, result);
        Assert.NotNull(node);
        Assert.Equal(NodeHealth.Online, node.Health);
        Assert.Equal(clock.GetUtcNow(), node.LastHeartbeatAtUtc);
        Assert.Equal(812, node.Metrics!.AvailableRamMiB);
    }

    [Fact]
    public void Rpc_worker_process_is_reported_separately_from_node_liveness()
    {
        var clock = new ManualTimeProvider();
        var registry = new NodeRegistry(clock, OfflineAfter);
        registry.Register(Node("a07-1"), out var awaiting);
        Assert.Equal(RpcWorkerProcessState.Unknown, awaiting!.RpcWorkerProcessState);

        registry.Heartbeat(
            "a07-1",
            new HeartbeatRequest(10, 700, 35, 2, RpcWorkerProcessState.Running),
            out var online);
        Assert.Equal(NodeHealth.Online, online!.Health);
        Assert.Equal(RpcWorkerProcessState.Running, online.RpcWorkerProcessState);

        clock.Advance(OfflineAfter.Add(TimeSpan.FromMilliseconds(1)));
        var offline = Assert.Single(registry.GetAll());
        Assert.Equal(NodeHealth.Offline, offline.Health);
        Assert.Equal(RpcWorkerProcessState.Unknown, offline.RpcWorkerProcessState);
    }

    [Fact]
    public void Heartbeat_rejects_undefined_rpc_worker_process_state()
    {
        var registry = NewRegistry();
        registry.Register(Node("a07-1"), out _);

        var result = registry.Heartbeat(
            "a07-1",
            new HeartbeatRequest(10, 700, 35, 2, (RpcWorkerProcessState)999),
            out var node);

        Assert.Equal(HeartbeatResult.Invalid, result);
        Assert.Null(node);
        Assert.Equal(NodeHealth.AwaitingHeartbeat, Assert.Single(registry.GetAll()).Health);
    }

    [Fact]
    public void Heartbeat_accepts_missing_temperature_sensor_reading()
    {
        var registry = NewRegistry();
        registry.Register(Node("a07-1"), out _);

        var result = registry.Heartbeat("a07-1", new HeartbeatRequest(30, 900, null, 12), out var node);

        Assert.Equal(HeartbeatResult.Accepted, result);
        Assert.Equal(NodeHealth.Online, node!.Health);
        Assert.Null(node.Metrics!.TemperatureCelsius);
    }

    [Fact]
    public void Node_becomes_offline_after_heartbeat_timeout()
    {
        var clock = new ManualTimeProvider();
        var registry = new NodeRegistry(clock, OfflineAfter);
        registry.Register(Node("a07-1"), out _);
        registry.Heartbeat("a07-1", new HeartbeatRequest(10, 700, 35, 2), out _);
        clock.Advance(TimeSpan.FromSeconds(6).Add(TimeSpan.FromMilliseconds(1)));

        var node = Assert.Single(registry.GetAll());

        Assert.Equal(NodeHealth.Offline, node.Health);
    }

    [Fact]
    public void Heartbeat_at_timeout_boundary_is_still_online()
    {
        var clock = new ManualTimeProvider();
        var registry = new NodeRegistry(clock, OfflineAfter);
        registry.Register(Node("a07-1"), out _);
        registry.Heartbeat("a07-1", new HeartbeatRequest(10, 700, 35, 2), out _);
        clock.Advance(OfflineAfter);

        var node = Assert.Single(registry.GetAll());

        Assert.Equal(NodeHealth.Online, node.Health);
    }

    [Fact]
    public void Explicit_disconnect_marks_node_offline_immediately()
    {
        var clock = new ManualTimeProvider();
        var registry = new NodeRegistry(clock, OfflineAfter);
        registry.Register(Node("a07-1"), out _);
        registry.Heartbeat("a07-1", new HeartbeatRequest(10, 700, 35, 2), out _);

        var result = registry.MarkDisconnected("a07-1", out var node);

        Assert.Equal(DisconnectResult.Disconnected, result);
        Assert.NotNull(node);
        Assert.Equal(NodeHealth.Offline, node.Health);
        Assert.Equal(clock.GetUtcNow(), node.LastHeartbeatAtUtc);
    }

    [Fact]
    public void Heartbeat_after_explicit_disconnect_restores_online_status()
    {
        var registry = NewRegistry();
        registry.Register(Node("a07-1"), out _);
        registry.Heartbeat("a07-1", new HeartbeatRequest(10, 700, 35, 2), out _);
        registry.MarkDisconnected("a07-1", out _);

        var result = registry.Heartbeat("a07-1", new HeartbeatRequest(12, 680, 36, 3), out var node);

        Assert.Equal(HeartbeatResult.Accepted, result);
        Assert.Equal(NodeHealth.Online, node!.Health);
    }

    [Fact]
    public void Explicit_disconnect_requires_registered_node()
    {
        var registry = NewRegistry();

        var result = registry.MarkDisconnected("missing", out var node);

        Assert.Equal(DisconnectResult.NotFound, result);
        Assert.Null(node);
    }

    [Theory]
    [InlineData(-1, 700, 35, 2)]
    [InlineData(101, 700, 35, 2)]
    [InlineData(10, -1, 35, 2)]
    [InlineData(10, 700, 121, 2)]
    [InlineData(10, 700, 35, -1)]
    public void Heartbeat_rejects_invalid_telemetry(double cpu, int ram, double temperature, double latency)
    {
        var registry = NewRegistry();
        registry.Register(Node("a07-1"), out _);

        var result = registry.Heartbeat("a07-1", new HeartbeatRequest(cpu, ram, temperature, latency), out var node);

        Assert.Equal(HeartbeatResult.Invalid, result);
        Assert.Null(node);
        Assert.Equal(NodeHealth.AwaitingHeartbeat, Assert.Single(registry.GetAll()).Health);
    }

    [Fact]
    public void Heartbeat_requires_registered_node()
    {
        var registry = NewRegistry();

        var result = registry.Heartbeat("missing", new HeartbeatRequest(10, 700, 35, 2), out var node);

        Assert.Equal(HeartbeatResult.NotFound, result);
        Assert.Null(node);
    }

    private static NodeRegistry NewRegistry() => new(new ManualTimeProvider(), OfflineAfter);

    private static RegisterNodeRequest Node(string? id) => new(id!, "Galaxy A07 test", "SM-A075M");

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }
}
