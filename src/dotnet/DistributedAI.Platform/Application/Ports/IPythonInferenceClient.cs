using System.Text.Json;

namespace DistributedAI.Platform;

public interface IPythonInferenceClient
{
    Task<JsonElement> ListModelsAsync(CancellationToken cancellationToken);
    Task<JsonElement> CompleteAsync(JsonElement request, CancellationToken cancellationToken);

    IAsyncEnumerable<ReadOnlyMemory<byte>> StreamCompletionAsync(
        JsonElement request,
        CancellationToken cancellationToken);
}

public sealed class PythonInferenceException(
    string message,
    int statusCode = StatusCodes.Status502BadGateway,
    string? responseBody = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public int StatusCode { get; } = statusCode;
    public string? ResponseBody { get; } = responseBody;
}
