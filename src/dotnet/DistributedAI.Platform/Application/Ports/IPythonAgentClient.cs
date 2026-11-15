using System.Text.Json;
using DistributedAI.Contracts;

namespace DistributedAI.Platform;

public interface IPythonAgentClient
{
    Task<JsonElement> GetHealthAsync(CancellationToken cancellationToken);
    Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken);

    IAsyncEnumerable<ReadOnlyMemory<byte>> StreamChatAsync(
        ChatRequest request,
        CancellationToken cancellationToken);
}

public sealed class PythonAgentUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
