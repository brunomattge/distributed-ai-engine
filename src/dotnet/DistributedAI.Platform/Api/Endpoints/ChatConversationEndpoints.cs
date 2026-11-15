using DistributedAI.Contracts;

namespace DistributedAI.Platform;

public static class ChatConversationEndpoints
{
    public static IEndpointRouteBuilder MapChatConversationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/conversations", ListRecentAsync);
        endpoints.MapGet("/api/conversations/{id:guid}", GetAsync);
        endpoints.MapPut("/api/conversations/{id:guid}", SaveAsync);
        return endpoints;
    }

    public static async Task<IResult> ListRecentAsync(
        IChatConversationStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await store.ListRecentAsync(cancellationToken));
        }
        catch (StorageUnavailableException)
        {
            return StorageUnavailable();
        }
    }

    public static async Task<IResult> GetAsync(
        Guid id,
        IChatConversationStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            var conversation = await store.GetAsync(id, cancellationToken);
            return conversation is null
                ? Results.NotFound(new ApiError("conversation_not_found", "Conversation was not found."))
                : Results.Ok(conversation);
        }
        catch (StorageUnavailableException)
        {
            return StorageUnavailable();
        }
    }

    public static async Task<IResult> SaveAsync(
        Guid id,
        SaveChatConversationRequest request,
        IChatConversationStore store,
        CancellationToken cancellationToken)
    {
        if (request.Messages is not { Count: > 0 and <= 200 } ||
            request.Messages.Any(message =>
                message is null ||
                message.Role is not ("user" or "assistant") ||
                string.IsNullOrWhiteSpace(message.Content) ||
                message.Content.Length > 12_000))
        {
            return Results.BadRequest(
                new ApiError("invalid_conversation", "Conversation must contain 1–200 valid user/assistant messages."));
        }

        try
        {
            return Results.Ok(await store.SaveAsync(id, request.Messages, cancellationToken));
        }
        catch (StorageUnavailableException)
        {
            return StorageUnavailable();
        }
    }

    private static IResult StorageUnavailable() =>
        Results.Json(
            new ApiError("storage_unavailable", "Conversation storage is unavailable."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
