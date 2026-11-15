using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DistributedAI.Contracts;

namespace DistributedAI.Platform;

public sealed class PythonAgentClient(HttpClient httpClient) : IPythonAgentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<JsonElement> GetHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync("api/health", cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new PythonAgentUnavailableException("The Python agent runtime could not be reached.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PythonAgentUnavailableException("The Python agent runtime health request timed out.", exception);
        }
    }

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var httpRequest = CreateChatRequest("api/chat", request);
            response = await httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new PythonAgentUnavailableException("The Python agent runtime could not be reached.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PythonAgentUnavailableException("The Python agent runtime request timed out.", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new PythonAgentUnavailableException(
                    $"The Python agent runtime returned HTTP {(int)response.StatusCode}.");
            }

            var result = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken);
            return result is { Reply.Length: > 0, ToolEvents: not null }
                ? result
                : throw new PythonAgentUnavailableException("The Python agent runtime returned an invalid chat response.");
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamChatAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/chat/stream")
        {
            Content = CreateJsonContent(request),
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new PythonAgentUnavailableException("The Python agent runtime could not be reached.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PythonAgentUnavailableException("The Python agent runtime request timed out.", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new PythonAgentUnavailableException(
                    $"The Python agent runtime returned HTTP {(int)response.StatusCode}.");
            }

            if (!string.Equals(
                    response.Content.Headers.ContentType?.MediaType,
                    "text/event-stream",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new PythonAgentUnavailableException("The Python agent runtime returned a non-stream response.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[4096];
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, cancellationToken);
                }
                catch (HttpRequestException exception)
                {
                    throw new PythonAgentUnavailableException("The Python agent stream was interrupted.", exception);
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new PythonAgentUnavailableException("The Python agent stream timed out.", exception);
                }

                if (read == 0)
                {
                    yield break;
                }

                yield return buffer.AsMemory(0, read).ToArray();
            }
        }
    }

    private static HttpRequestMessage CreateChatRequest(string path, ChatRequest request) =>
        new(HttpMethod.Post, path) { Content = CreateJsonContent(request) };

    private static ByteArrayContent CreateJsonContent(ChatRequest request)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
}
