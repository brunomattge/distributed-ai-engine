using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DistributedAI.Platform;

public sealed class PythonInferenceClient(HttpClient httpClient) : IPythonInferenceClient
{
    public async Task<JsonElement> ListModelsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var response = await SendAsync(request, cancellationToken);
        return await ReadJsonAsync(response, cancellationToken);
    }

    public async Task<JsonElement> CompleteAsync(JsonElement request, CancellationToken cancellationToken)
    {
        using var httpRequest = CreateRequest(request);
        using var response = await SendAsync(httpRequest, cancellationToken);
        return await ReadJsonAsync(response, cancellationToken);
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamCompletionAsync(
        JsonElement request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var httpRequest = CreateRequest(request);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

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
            throw new PythonInferenceException("The Python inference runtime could not be reached.", innerException: exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PythonInferenceException("The Python inference request timed out.", innerException: exception, isTimeout: true);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw CreateResponseException(response.StatusCode, body);
            }

            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                throw new PythonInferenceException("The Python inference runtime returned a non-SSE stream.");
            }

            Stream stream;
            try
            {
                stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            }
            catch (IOException exception)
            {
                throw new PythonInferenceException("The Python inference stream was interrupted.", innerException: exception);
            }

            await using (stream)
            {
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
                        throw new PythonInferenceException("The Python inference stream was interrupted.", innerException: exception);
                    }
                    catch (IOException exception)
                    {
                        throw new PythonInferenceException("The Python inference stream was interrupted.", innerException: exception);
                    }
                    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new PythonInferenceException("The Python inference stream timed out.", innerException: exception);
                    }

                    if (read == 0) yield break;
                    yield return buffer.AsMemory(0, read).ToArray();
                }
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new PythonInferenceException("The Python inference runtime could not be reached.", innerException: exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PythonInferenceException("The Python inference request timed out.", innerException: exception, isTimeout: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var exception = CreateResponseException(response.StatusCode, body);
            response.Dispose();
            throw exception;
        }

        return response;
    }

    private static HttpRequestMessage CreateRequest(JsonElement request) => new(HttpMethod.Post, "v1/chat/completions")
    {
        Content = new StringContent(request.GetRawText(), Encoding.UTF8, "application/json"),
    };

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new PythonInferenceException("The Python inference runtime returned invalid JSON.", innerException: exception);
        }
    }

    private static PythonInferenceException CreateResponseException(HttpStatusCode statusCode, string body) =>
        new($"The Python inference runtime returned HTTP {(int)statusCode}.", (int)statusCode, body);
}
