using System.Net;
using System.IO;
using System.Text;
using System.Text.Json;
using DistributedAI.Platform;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DistributedAI.Platform.Tests;

public sealed class PythonInferenceClientTests
{
    [Fact]
    public async Task ListModelsAsync_reads_the_versioned_python_route()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonResponse("""{"object":"list","data":[{"id":"qwen-fast"}]}"""));
        });
        var client = CreateClient(handler);

        var models = await client.ListModelsAsync(CancellationToken.None);

        Assert.Equal("qwen-fast", models.GetProperty("data")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task CompleteAsync_posts_json_and_reads_the_provider_response()
    {
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.True(request.Content!.Headers.ContentLength > 0);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal("qwen-fast", body.RootElement.GetProperty("model").GetString());
            return JsonResponse("""{"object":"chat.completion","choices":[{"message":{"content":"Hello"}}]}""");
        });
        var client = CreateClient(handler);
        using var request = JsonDocument.Parse("""{"model":"qwen-fast","messages":[{"role":"user","content":"Hi"}]}""");

        var response = await client.CompleteAsync(request.RootElement, CancellationToken.None);

        Assert.Equal("Hello", response.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task StreamCompletionAsync_forwards_sse_bytes()
    {
        const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\ndata: [DONE]\n\n";
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        }));
        var client = CreateClient(handler);
        using var request = JsonDocument.Parse("""{"model":"qwen-fast","stream":true}""");

        var chunks = await CollectAsync(client.StreamCompletionAsync(request.RootElement, CancellationToken.None));

        Assert.Contains("[DONE]", Encoding.UTF8.GetString(chunks.SelectMany(chunk => chunk.ToArray()).ToArray()));
    }

    [Fact]
    public async Task StreamCompletionAsync_maps_a_midstream_io_failure_to_interrupted_stream()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ThrowAfterFirstReadStream("""data: {"choices":[]}""" + "\n\n")),
        }));
        handler.ResponseContentType = "text/event-stream";
        var client = CreateClient(handler);
        using var request = JsonDocument.Parse("""{"model":"qwen-fast","stream":true}""");

        var exception = await Assert.ThrowsAsync<PythonInferenceException>(async () =>
            await CollectAsync(client.StreamCompletionAsync(request.RootElement, CancellationToken.None)));

        Assert.Contains("stream was interrupted", exception.Message);
    }

    [Fact]
    public async Task CompleteAsync_preserves_upstream_status_and_error_body()
    {
        var client = CreateClient(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("{\"error\":{\"code\":\"tools_not_supported\"}}", Encoding.UTF8, "application/json"),
        })));
        using var request = JsonDocument.Parse("{} ");

        var exception = await Assert.ThrowsAsync<PythonInferenceException>(() => client.CompleteAsync(request.RootElement, CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, exception.StatusCode);
        Assert.Contains("tools_not_supported", exception.ResponseBody);
    }

    private static PythonInferenceClient CreateClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8000/") });

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static async Task<IReadOnlyList<ReadOnlyMemory<byte>>> CollectAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> stream)
    {
        var chunks = new List<ReadOnlyMemory<byte>>();
        await foreach (var chunk in stream) chunks.Add(chunk);
        return chunks;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public string? ResponseContentType { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            SetContentTypeAsync(request, cancellationToken);

        private async Task<HttpResponseMessage> SetContentTypeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await send(request, cancellationToken);
            if (ResponseContentType is not null)
            {
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ResponseContentType);
            }
            return response;
        }
    }

    private sealed class ThrowAfterFirstReadStream(string initialContent) : MemoryStream(Encoding.UTF8.GetBytes(initialContent))
    {
        private bool _hasRead;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureSingleRead();
            return base.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            EnsureSingleRead();
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }

        private void EnsureSingleRead()
        {
            if (_hasRead) throw new IOException("Simulated connection reset.");
            _hasRead = true;
        }
    }
}
