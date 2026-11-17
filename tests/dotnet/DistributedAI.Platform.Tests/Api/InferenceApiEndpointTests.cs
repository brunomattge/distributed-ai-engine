using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DistributedAI.Contracts;
using DistributedAI.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DistributedAI.Platform.Tests;

public sealed class InferenceApiEndpointTests
{
    private static readonly IServiceProvider HttpServices = new ServiceCollection()
        .AddLogging()
        .AddRouting()
        .BuildServiceProvider();

    [Fact]
    public async Task ListModelsAsync_returns_the_engine_model_list()
    {
        var client = new StubInferenceClient("""{"object":"list","data":[{"id":"qwen-fast"}]}""");

        var result = await InferenceApiEndpoints.ListModelsAsync(client, CancellationToken.None);
        var (status, body) = await ExecuteAsync(result);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains("qwen-fast", body);
    }

    [Fact]
    public async Task CompleteAsync_proxies_success_only_when_distributed_inference_is_ready()
    {
        var client = new StubInferenceClient("""{"object":"chat.completion","choices":[{"message":{"content":"Hello"}}]}""");
        var request = Parse("""{"model":"qwen-fast","messages":[{"role":"user","content":"Hi"}]}""");
        var context = NewHttpContext();

        await InferenceApiEndpoints.CompleteAsync(request, client, new StubReadiness(true), context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(1, client.CompletionCalls);
        Assert.Contains("Hello", await ReadBodyAsync(context));
    }

    [Fact]
    public async Task CompleteAsync_rejects_unavailable_cluster_before_calling_python()
    {
        var client = new StubInferenceClient("{}");
        var readiness = new StubReadiness(false);
        var request = Parse("""{"model":"qwen-fast","messages":[{"role":"user","content":"Hi"}]}""");
        var context = NewHttpContext();

        await InferenceApiEndpoints.CompleteAsync(request, client, readiness, context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal(0, client.CompletionCalls);
        Assert.Contains("inference_unavailable", await ReadBodyAsync(context));
    }

    [Fact]
    public async Task CompleteAsync_marks_degraded_when_python_stream_emits_error_across_chunks()
    {
        var client = new StubInferenceClient("{}")
        {
            StreamChunks = [
                Encoding.UTF8.GetBytes("data: {\"err"),
                Encoding.UTF8.GetBytes("or\":{\"message\":\"RPC failed\"}}\n\n"),
            ],
        };
        var readiness = new StubReadiness(true);
        var request = Parse("""{"model":"qwen-fast","stream":true}""");
        var context = NewHttpContext();

        await InferenceApiEndpoints.CompleteAsync(request, client, readiness, context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(readiness.FailureReported);
        Assert.Contains("RPC failed", await ReadBodyAsync(context));
    }

    [Fact]
    public async Task CompleteAsync_marks_degraded_and_blocks_next_request_when_stream_lacks_done_marker()
    {
        var client = new StubInferenceClient("""{"object":"chat.completion"}""")
        {
            StreamChunks = [Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n")],
        };
        var readiness = new StubReadiness(true);
        var streamRequest = Parse("""{"model":"qwen-fast","stream":true}""");
        var streamContext = NewHttpContext();

        await InferenceApiEndpoints.CompleteAsync(streamRequest, client, readiness, streamContext);

        Assert.Equal(StatusCodes.Status200OK, streamContext.Response.StatusCode);
        Assert.True(readiness.FailureReported);
        var streamBody = await ReadBodyAsync(streamContext);
        Assert.Contains("ended before its completion marker", streamBody);
        Assert.EndsWith("data: [DONE]\n\n", streamBody);

        var completionRequest = Parse("""{"model":"qwen-fast","messages":[{"role":"user","content":"Try again"}]}""");
        var completionContext = NewHttpContext();
        await InferenceApiEndpoints.CompleteAsync(completionRequest, client, readiness, completionContext);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, completionContext.Response.StatusCode);
        Assert.Equal(0, client.CompletionCalls);
    }

    [Fact]
    public async Task CompleteAsync_emits_an_error_and_degrades_after_a_midstream_transport_failure()
    {
        var client = new StubInferenceClient("{}")
        {
            StreamChunks = [Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n")],
            StreamFailure = new PythonInferenceException("The Python inference stream was interrupted."),
        };
        var readiness = new StubReadiness(true);
        var request = Parse("""{"model":"qwen-fast","stream":true}""");
        var context = NewHttpContext();

        await InferenceApiEndpoints.CompleteAsync(request, client, readiness, context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(readiness.FailureReported);
        var body = await ReadBodyAsync(context);
        Assert.Contains("Distributed inference failed", body);
        Assert.EndsWith("data: [DONE]\n\n", body);
    }

    [Fact]
    public async Task CompleteAsync_passes_client_error_without_marking_session_degraded()
    {
        var client = new StubInferenceClient("{}")
        {
            CompletionFailure = new PythonInferenceException(
                "Invalid request", StatusCodes.Status422UnprocessableEntity, "{\"error\":{\"code\":\"tools_not_supported\"}}"),
        };
        var readiness = new StubReadiness(true);
        var request = Parse("""{"model":"qwen-fast","messages":[{"role":"user","content":"Hi"}]}""");
        var context = NewHttpContext();

        await InferenceApiEndpoints.CompleteAsync(request, client, readiness, context);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, context.Response.StatusCode);
        Assert.False(readiness.FailureReported);
        Assert.Contains("tools_not_supported", await ReadBodyAsync(context));
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static DefaultHttpContext NewHttpContext()
    {
        var context = new DefaultHttpContext();
        context.RequestServices = HttpServices;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private static async Task<(int StatusCode, string Body)> ExecuteAsync(IResult result)
    {
        var context = NewHttpContext();
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, await ReadBodyAsync(context));
    }

    private sealed class StubReadiness(bool available) : IInferenceReadiness
    {
        public bool FailureReported { get; private set; }

        public Task<InferenceStatus> GetAsync(CancellationToken cancellationToken)
        {
            var isAvailable = available && !FailureReported;
            return Task.FromResult(new InferenceStatus(
                isAvailable,
                isAvailable ? "ready" : FailureReported ? "degraded" : "cluster_incomplete",
                isAvailable ? "ready" : "unavailable",
                3,
                3,
                "qwen-fast",
                "Qwen fast",
                isAvailable ? null : FailureReported ? "Session degraded." : "Reconnect all workers."));
        }

        public void ReportSessionFailure() => FailureReported = true;
    }

    private sealed class StubInferenceClient(string completionJson) : IPythonInferenceClient
    {
        public int CompletionCalls { get; private set; }
        public PythonInferenceException? CompletionFailure { get; init; }
        public PythonInferenceException? StreamFailure { get; init; }
        public IReadOnlyList<byte[]> StreamChunks { get; init; } = [Encoding.UTF8.GetBytes("data: [DONE]\n\n")];

        public Task<JsonElement> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Parse("""{"object":"list","data":[{"id":"qwen-fast"}]}"""));

        public Task<JsonElement> CompleteAsync(JsonElement request, CancellationToken cancellationToken)
        {
            CompletionCalls++;
            if (CompletionFailure is not null) throw CompletionFailure;
            return Task.FromResult(Parse(completionJson));
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamCompletionAsync(
            JsonElement request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var chunk in StreamChunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return chunk;
                await Task.Yield();
            }

            if (StreamFailure is not null) throw StreamFailure;
        }
    }
}
