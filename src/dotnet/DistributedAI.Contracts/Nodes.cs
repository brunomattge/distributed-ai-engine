using System.Text.Json.Serialization;

namespace DistributedAI.Contracts;

public sealed record RegisterNodeRequest(
    string NodeId,
    string Name,
    string HardwareModel);

public sealed record HeartbeatRequest(
    double CpuPercent,
    int AvailableRamMiB,
    double? TemperatureCelsius,
    double LatencyMs,
    RpcWorkerProcessState RpcWorkerProcessState = RpcWorkerProcessState.Unknown);

public enum NodeHealth
{
    AwaitingHeartbeat,
    Online,
    Offline
}

public enum RpcWorkerProcessState
{
    Unknown,
    Running,
    Stopped
}

public sealed record NodeSnapshot(
    string NodeId,
    string Name,
    string HardwareModel,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset? LastHeartbeatAtUtc,
    NodeHealth Health,
    RpcWorkerProcessState RpcWorkerProcessState,
    HeartbeatRequest? Metrics);

public sealed record InferenceStatus(
    bool Available,
    string State,
    string CoordinatorState,
    int ActiveRpcWorkers,
    int RequiredWorkers,
    string ModelId,
    string ModelName,
    string? Message);

public sealed record ApiError(string Code, string Message);

public sealed record ChatMessage(string Role, string Content);

public sealed record ChatRequest(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<string>? Memories = null);

public sealed record ChatToolEvent(string Tool, bool Healthy);

public sealed record ChatResponse(
    string Reply,
    [property: JsonPropertyName("tool_events")] IReadOnlyList<ChatToolEvent> ToolEvents);

public sealed record SaveChatConversationRequest(IReadOnlyList<ChatMessage> Messages);

public sealed record ChatConversation(
    Guid Id,
    IReadOnlyList<ChatMessage> Messages,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ChatConversationSummary(Guid Id, string Title, DateTimeOffset UpdatedAtUtc);

public sealed record AgentMemory(Guid Id, string Content, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record SaveAgentMemoryRequest(string Content);
