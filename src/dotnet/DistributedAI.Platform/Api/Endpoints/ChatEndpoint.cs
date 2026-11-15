using System.Text;
using System.Text.Json;
using DistributedAI.Contracts;

namespace DistributedAI.Platform;

public static class ChatEndpoint
{
    public static RouteHandlerBuilder MapChatEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/chat", HandleWithReadinessAsync);

    public static RouteHandlerBuilder MapChatStreamEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/chat/stream", StreamWithReadinessAsync);

    public static async Task<IResult> HandleWithReadinessAsync(
        ChatRequest request,
        IPythonAgentClient agent,
        IAgentMemoryStore memoryStore,
        IInferenceReadiness readiness,
        CancellationToken cancellationToken)
    {
        if (!IsValid(request))
        {
            return Results.BadRequest(new ApiError("invalid_chat", "At least one valid chat message is required."));
        }

        var status = await readiness.GetAsync(cancellationToken);
        if (!status.Available)
        {
            return Results.Json(
                new ApiError("inference_unavailable", status.Message ?? "Distributed inference is unavailable."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return await HandleCoreAsync(request, agent, memoryStore, readiness, cancellationToken);
    }

    public static async Task StreamWithReadinessAsync(
        ChatRequest request,
        IPythonAgentClient agent,
        IAgentMemoryStore memoryStore,
        IInferenceReadiness readiness,
        HttpContext context)
    {
        if (!IsValid(request))
        {
            await Results.BadRequest(new ApiError("invalid_chat", "At least one valid chat message is required."))
                .ExecuteAsync(context);
            return;
        }

        var status = await readiness.GetAsync(context.RequestAborted);
        if (!status.Available)
        {
            await Results.Json(
                new ApiError("inference_unavailable", status.Message ?? "Distributed inference is unavailable."),
                statusCode: StatusCodes.Status503ServiceUnavailable)
                .ExecuteAsync(context);
            return;
        }

        await StreamCoreAsync(request, agent, memoryStore, readiness, context);
    }

    public static async Task<IResult> HandleAsync(
        ChatRequest request,
        IPythonAgentClient agent,
        IAgentMemoryStore memoryStore,
        CancellationToken cancellationToken) =>
        await HandleCoreAsync(request, agent, memoryStore, null, cancellationToken);

    private static async Task<IResult> HandleCoreAsync(
        ChatRequest request,
        IPythonAgentClient agent,
        IAgentMemoryStore memoryStore,
        IInferenceReadiness? readiness,
        CancellationToken cancellationToken)
    {
        if (!IsValid(request))
        {
            return Results.BadRequest(new ApiError("invalid_chat", "At least one valid chat message is required."));
        }

        try
        {
            var memories = await TryLoadMemoriesAsync(memoryStore, cancellationToken);
            request = request with { Memories = memories ?? [] };
            return Results.Ok(await agent.ChatAsync(request, cancellationToken));
        }
        catch (PythonAgentUnavailableException)
        {
            readiness?.ReportSessionFailure();
            var message = readiness is null
                ? "The Python agent runtime is unavailable."
                : "The RPC session failed. The supervisor is recovering the cluster; wait before sending another message.";
            return Results.Json(
                new ApiError(readiness is null ? "agent_unavailable" : "inference_degraded", message),
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    public static async Task StreamAsync(
        ChatRequest request,
        IPythonAgentClient agent,
        IAgentMemoryStore memoryStore,
        HttpContext context) =>
        await StreamCoreAsync(request, agent, memoryStore, null, context);

    private static async Task StreamCoreAsync(
        ChatRequest request,
        IPythonAgentClient agent,
        IAgentMemoryStore memoryStore,
        IInferenceReadiness? readiness,
        HttpContext context)
    {
        if (!IsValid(request))
        {
            await Results.BadRequest(
                new ApiError("invalid_chat", "At least one valid chat message is required."))
                .ExecuteAsync(context);
            return;
        }

        var memories = await TryLoadMemoriesAsync(memoryStore, context.RequestAborted);
        request = request with { Memories = memories ?? [] };
        if (memories is null)
        {
            context.Response.Headers["X-Agent-Memory-Status"] = "unavailable";
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            var pendingEventText = new StringBuilder();
            await foreach (var chunk in agent.StreamChatAsync(request, context.RequestAborted))
            {
                if (ContainsSseErrorEvent(chunk, pendingEventText)) readiness?.ReportSessionFailure();
                await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        catch (PythonAgentUnavailableException)
        {
            readiness?.ReportSessionFailure();
            var message = readiness is null
                ? "The Python agent runtime is unavailable."
                : "The RPC session failed. The supervisor is recovering the cluster; wait before sending another message.";
            if (context.Response.HasStarted)
            {
                var eventBytes = Encoding.UTF8.GetBytes($"event: error\ndata: {JsonSerializer.Serialize(new { error = message })}\n\n");
                await context.Response.Body.WriteAsync(eventBytes, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                return;
            }

            context.Response.Clear();
            await Results.Json(
                new ApiError(readiness is null ? "agent_unavailable" : "inference_degraded", message),
                statusCode: StatusCodes.Status502BadGateway)
                .ExecuteAsync(context);
        }
    }

    private static async Task<IReadOnlyList<string>?> TryLoadMemoriesAsync(
        IAgentMemoryStore memoryStore,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await memoryStore.ListAsync(cancellationToken)).Select(memory => memory.Content).ToArray();
        }
        catch (StorageUnavailableException)
        {
            return null;
        }
    }

    private static bool ContainsSseErrorEvent(ReadOnlyMemory<byte> chunk, StringBuilder pendingText)
    {
        pendingText.Append(Encoding.UTF8.GetString(chunk.Span));
        var text = pendingText.ToString();
        var foundError = false;
        var separator = text.IndexOf("\n\n", StringComparison.Ordinal);
        while (separator >= 0)
        {
            var eventText = text[..separator];
            if (eventText.Split('\n').Any(line => line.TrimEnd('\r') == "event: error"))
            {
                foundError = true;
            }

            text = text[(separator + 2)..];
            separator = text.IndexOf("\n\n", StringComparison.Ordinal);
        }

        pendingText.Clear();
        pendingText.Append(text);
        return foundError;
    }

    private static bool IsValid(ChatRequest request) =>
        request.Messages is { Count: > 0 } && !request.Messages.Any(message =>
            message is null || string.IsNullOrWhiteSpace(message.Role) || message.Content is null);
}
