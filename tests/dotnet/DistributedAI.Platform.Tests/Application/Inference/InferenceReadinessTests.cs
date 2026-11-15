using System.Runtime.CompilerServices;
using System.Text.Json;
using DistributedAI.Contracts;
using DistributedAI.Platform;
using Xunit;

namespace DistributedAI.Platform.Tests;

public sealed class InferenceReadinessTests
{
    [Fact]
    public async Task Reports_chat_available_when_coordinator_and_all_RPC_processes_are_active()
    {
        var status = await CreateService("ready", activeRpcWorkers: 3).GetAsync(CancellationToken.None);

        Assert.True(status.Available);
        Assert.Equal("ready", status.State);
        Assert.Equal(3, status.ActiveRpcWorkers);
        Assert.Equal(3, status.RequiredWorkers);
        Assert.Equal("Qwen fast", status.ModelName);
    }

    [Fact]
    public async Task Serializes_process_count_as_active_rpc_workers_not_model_ready_workers()
    {
        var status = await CreateService("ready", activeRpcWorkers: 3).GetAsync(CancellationToken.None);
        var json = JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"activeRpcWorkers\":3", json);
        Assert.DoesNotContain("readyWorkers", json);
    }

    [Fact]
    public async Task Reports_cluster_incomplete_when_coordinator_is_ready_but_an_RPC_process_is_missing()
    {
        var status = await CreateService("ready", activeRpcWorkers: 1).GetAsync(CancellationToken.None);

        Assert.False(status.Available);
        Assert.Equal("cluster_incomplete", status.State);
        Assert.Equal("ready", status.CoordinatorState);
        Assert.Equal(1, status.ActiveRpcWorkers);
        Assert.Contains("1/3", status.Message);
    }

    [Fact]
    public async Task Reports_loading_even_when_all_workers_are_online()
    {
        var status = await CreateService("loading", activeRpcWorkers: 3).GetAsync(CancellationToken.None);

        Assert.False(status.Available);
        Assert.Equal("loading", status.State);
        Assert.Equal(3, status.ActiveRpcWorkers);
    }

    [Fact]
    public async Task Does_not_treat_simulator_nodes_as_android_workers()
    {
        var registry = new NodeRegistry(TimeProvider.System, TimeSpan.FromSeconds(6));
        for (var index = 0; index < 3; index++)
        {
            var id = $"sim-test-{index}";
            registry.Register(new RegisterNodeRequest(id, $"Simulator {index}", "simulated Android"), out _);
            registry.Heartbeat(id, new HeartbeatRequest(10, 1000, 35, 5, RpcWorkerProcessState.Running), out _);
        }

        var service = new InferenceReadinessService(registry, new StubAgentClient("ready", unavailable: false));
        var status = await service.GetAsync(CancellationToken.None);

        Assert.False(status.Available);
        Assert.Equal("cluster_incomplete", status.State);
        Assert.Equal(0, status.ActiveRpcWorkers);
    }

    [Fact]
    public async Task Reports_unavailable_when_python_runtime_cannot_be_reached()
    {
        var status = await CreateService("offline", activeRpcWorkers: 3, unavailable: true).GetAsync(CancellationToken.None);

        Assert.False(status.Available);
        Assert.Equal("unavailable", status.State);
    }

    [Fact]
    public async Task Keeps_failed_session_degraded_until_supervisor_publishes_a_new_ready_generation()
    {
        var agent = new StubAgentClient("ready", unavailable: false) { SupervisorGeneration = 1 };
        var service = new InferenceReadinessService(CreateRegistry(3), agent);
        Assert.True((await service.GetAsync(CancellationToken.None)).Available);

        service.ReportSessionFailure();
        var beforeRestart = await service.GetAsync(CancellationToken.None);
        Assert.False(beforeRestart.Available);
        Assert.Equal("degraded", beforeRestart.State);

        agent.CoordinatorState = "unavailable";
        agent.SupervisorState = "waiting_for_devices";
        var waiting = await service.GetAsync(CancellationToken.None);
        Assert.False(waiting.Available);
        Assert.Equal("degraded", waiting.State);

        agent.CoordinatorState = "ready";
        agent.SupervisorState = "ready";
        agent.SupervisorGeneration = 2;
        var recovered = await service.GetAsync(CancellationToken.None);
        Assert.True(recovered.Available);
        Assert.Equal("ready", recovered.State);
    }

    private static IInferenceReadiness CreateService(string coordinatorState, int activeRpcWorkers, bool unavailable = false)
    {
        var registry = CreateRegistry(activeRpcWorkers);
        return new InferenceReadinessService(registry, new StubAgentClient(coordinatorState, unavailable));
    }

    private static NodeRegistry CreateRegistry(int activeRpcWorkers)
    {
        var registry = new NodeRegistry(TimeProvider.System, TimeSpan.FromSeconds(6));
        for (var index = 0; index < 3; index++)
        {
            var id = $"adb-test-{index}";
            registry.Register(new RegisterNodeRequest(id, $"Phone {index}", "Samsung test"), out _);
            if (index < activeRpcWorkers)
            {
                registry.Heartbeat(id, new HeartbeatRequest(10, 1000, 35, 5, RpcWorkerProcessState.Running), out _);
            }
            else if (index == activeRpcWorkers)
            {
                registry.MarkDisconnected(id, out _);
            }
        }

        return registry;
    }

    private sealed class StubAgentClient(string coordinatorState, bool unavailable) : IPythonAgentClient
    {
        public string CoordinatorState { get; set; } = coordinatorState;
        public string SupervisorState { get; set; } = "ready";
        public long SupervisorGeneration { get; set; }

        public Task<JsonElement> GetHealthAsync(CancellationToken cancellationToken) => unavailable
            ? Task.FromException<JsonElement>(new PythonAgentUnavailableException("offline"))
            : Task.FromResult(JsonDocument.Parse(
                $"{{\"state\":\"{CoordinatorState}\",\"model_id\":\"qwen-fast\",\"model_name\":\"Qwen fast\",\"supervisor_state\":\"{SupervisorState}\",\"supervisor_generation\":{SupervisorGeneration}}}")
                .RootElement.Clone());

        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamChatAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
