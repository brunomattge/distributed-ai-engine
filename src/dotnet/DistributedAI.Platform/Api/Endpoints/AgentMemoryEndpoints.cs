using DistributedAI.Contracts;

namespace DistributedAI.Platform;

public static class AgentMemoryEndpoints
{
    public static IEndpointRouteBuilder MapAgentMemoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/memories", ListAsync);
        endpoints.MapPost("/api/memories", CreateAsync);
        endpoints.MapDelete("/api/memories/{id:guid}", DeleteAsync);
        return endpoints;
    }

    public static async Task<IResult> ListAsync(
        IAgentMemoryStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await store.ListAsync(cancellationToken));
        }
        catch (StorageUnavailableException)
        {
            return StorageUnavailable();
        }
    }

    public static async Task<IResult> CreateAsync(
        SaveAgentMemoryRequest request,
        IAgentMemoryStore store,
        CancellationToken cancellationToken)
    {
        var content = request.Content?.Trim();
        if (string.IsNullOrWhiteSpace(content) || content.Length > 1200)
        {
            return Results.BadRequest(
                new ApiError("invalid_memory", "Memory must contain 1–1200 non-blank characters."));
        }

        try
        {
            var memory = await store.CreateAsync(content, cancellationToken);
            return memory is null
                ? Results.Conflict(new ApiError("memory_limit_reached", "At most 10 saved memories are allowed."))
                : Results.Created($"/api/memories/{memory.Id}", memory);
        }
        catch (StorageUnavailableException)
        {
            return StorageUnavailable();
        }
    }

    public static async Task<IResult> DeleteAsync(
        Guid id,
        IAgentMemoryStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            return await store.DeleteAsync(id, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound(new ApiError("memory_not_found", "Memory was not found."));
        }
        catch (StorageUnavailableException)
        {
            return StorageUnavailable();
        }
    }

    private static IResult StorageUnavailable() =>
        Results.Json(
            new ApiError("storage_unavailable", "Memory storage is unavailable."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
