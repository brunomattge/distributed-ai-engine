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

        var jobs = await InvokeAsync(request, client, new StubReadiness(true), context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(1, client.CompletionCalls);
        Assert.Contains("Hello", await ReadBodyAsync(context));
        Assert.Equal(InferenceWorkloadModes.SingleModelDistributed, context.Response.Headers["X-Distributed-AI-Workload-Mode"]);
        Assert.Equal(InferenceWorkloadScheduler.RpcBackend, context.Response.Headers["X-Distributed-AI-Backend"]);
        Assert.Equal("adb-test-0,adb-test-1,adb-test-2", context.Response.Headers["X-Distributed-AI-Selected-Nodes"]);

        var jobId = Guid.Parse(context.Response.Headers["X-Distributed-AI-Job-Id"]!);
        var job = jobs.Get(jobId);
        Assert.NotNull(job);
        Assert.Equal(InferenceJobStates.Completed, job.State);
        Assert.Equal("qwen-fast", job.ModelId);

        var (jobStatus, jobBody) = await ExecuteAsync(InferenceApiEndpoints.GetJobAsync(jobId, jobs));
        Assert.Equal(StatusCodes.Status200OK, jobStatus);
        Assert.Contains("completed", jobBody);
    }

    [Fact]
    public async Task CompleteAsync_rejects_independent_replica_mode_without_claiming_distributed_inference()
    {
        var client = new StubInferenceClient("{}");
        var request = Parse("""{"workload_mode":"independent_replicas","model":"qwen-fast","messages":[{"role":"user","content":"Hi"}]}""");
        var context = NewHttpContext();

        var jobs = await InvokeAsync(request, client, new StubReadiness(true), context);

        Assert.Equal(StatusCodes.Status501NotImplemented, context.Response.StatusCode);
        Assert.Equal(0, client.CompletionCalls);
        Assert.Contains("not executed by this endpoint", await ReadBodyAsync(context));
        Assert.Null(jobs.Get(Guid.Empty));
    }

    [Fact]
    public async Task CompleteAsync_rejects_a_model_that_is_not_loaded_before_job_admission()
    {
        var client = new StubInferenceClient("{}");
        var request = Parse("""{"model":"qwen-large","messages":[{"role":"user","content":"Hi"}]}""");
        var context = NewHttpContext();

        await InvokeAsync(request, client, new StubReadiness(true), context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal(0, client.CompletionCalls);
        Assert.Contains("model_not_found", await ReadBodyAsync(context));
        Assert.False(context.Response.Headers.ContainsKey("X-Distributed-AI-Job-Id"));
    }

    [Fact]
    public async Task CompleteAsync_tracks_concurrent_requests_as_separate_single_model_jobs()
    {
        var client = new StubInferenceClient("""{"object":"chat.completion"}""");
        var jobs = new InferenceJobStore(TimeProvider.System);
        var readiness = new StubReadiness(true);
        var firstContext = NewHttpContext();
        var secondContext = NewHttpContext();
        var first = Parse("""{"model":"qwen-fast","messages":[{"role":"user","content":"First"}]}""");
        var second = Parse("""{"model":"qwen-fast","messages":[{"role":"user","content":"Second"}]}""");

        await Task.WhenAll(
            InvokeAsync(first, client, readiness, firstContext, jobs),
            InvokeAsync(second, client, readiness, secondContext, jobs));

        var firstId = Guid.Parse(firstContext.Response.Headers["X-Distributed-AI-Job-Id"]!);
        var secondId = Guid.Parse(secondContext.Response.Headers["X-Distributed-AI-Job-Id"]!);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(InferenceWorkloadModes.SingleModelDistributed, jobs.Get(firstId)!.WorkloadMode);
        Assert.Equal(InferenceWorkloadModes.SingleModelDistributed, jobs.Get(secondId)!.WorkloadMode);
        Assert.Equal(InferenceJobStates.Completed, jobs.Get(firstId)!.State);
        Assert.Equal(InferenceJobStates.Completed, jobs.Get(secondId)!.State);
    }

    [Fact]
    public async Task CompleteAsync_records_runtime_timeout_as_timed_out()
    {
        var client = new StubInferenceClient("{}")
        {
            CompletionFailure = new PythonInferenceException("Runtime timed out.", isTimeout: true),
        };
        var context = NewHttpContext();
        var jobs = await InvokeAsync(
            Parse("""{"model":"qwen-fast","messages":[{"role":"user","content":"Hi"}]}"""),
            client,
            new StubReadiness(true),
            context);

        var jobId = Guid.Parse(context.Response.Headers["X-Distributed-AI-Job-Id"]!);
        Assert.Equal(InferenceJobStates.TimedOut, jobs.Get(jobId)!.State);
        Assert.Equal(1, client.CompletionCalls);
    }

    [Fact]
    public async Task CompleteAsync_records_client_cancellation_without_degrading_the_cluster()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var readiness = new StubReadiness(true);
        var context = NewHttpContext();
        context.RequestAborted = cancellation.Token;

        var jobs = await InvokeAsync(
            Parse("""{"model":"qwen-fast","messages":[{"role":"user","content":"Hi"}]}"""),
            new StubInferenceClient("{}"),
            readiness,
            context);

        var jobId = Guid.Parse(context.Response.Headers["X-Distributed-AI-Job-Id"]!);
        Assert.Equal(InferenceJobStates.Cancelled, jobs.Get(jobId)!.State);
        Assert.False(readiness.FailureReported);
    }

    [Fact]
    public async Task CompleteAsync_fails_admission_when_a_worker_is_missing()
    {
        var client = new StubInferenceClient("{}");
        var readiness = new StubReadiness(true);
        var context = NewHttpContext();
        var registry = CreateReadyNodeRegistry();
        registry.MarkDisconnected("adb-test-2", out _);

        await InvokeAsync(
            Parse("""{"model":"qwen-fast","messages":[{"role":"user","content":"Hi"}]}"""),
            client,
            readiness,
            context,
            nodes: registry);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal(0, client.CompletionCalls);
        Assert.Contains("placement_unavailable", await ReadBodyAsync(context));
        Assert.False(context.Response.Headers.ContainsKey("X-Distributed-AI-Job-Id"));
    }

    [Fact]
    public async Task CompleteAsync_rejects_unavailable_cluster_before_calling_python()
    {
        var client = new StubInferenceClient("{}");
        var readiness = new StubReadiness(false);
        var request = Parse("""{"model":"qwen-fast","messages":[{"role":"user","content":"Hi"}]}""");
        var context = NewHttpContext();

        await InvokeAsync(request, client, readiness, context);

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

        await InvokeAsync(request, client, readiness, context);

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

        await InvokeAsync(streamRequest, client, readiness, streamContext);

        Assert.Equal(StatusCodes.Status200OK, streamContext.Response.StatusCode);
        Assert.True(readiness.FailureReported);
        var streamBody = await ReadBodyAsync(streamContext);
        Assert.Contains("ended before its completion marker", streamBody);
        Assert.EndsWith("data: [DONE]\n\n", streamBody);

        var completionRequest = Parse("""{"model":"qwen-fast","messages":[{"role":"user","content":"Try again"}]}""");
        var completionContext = NewHttpContext();
        await InvokeAsync(completionRequest, client, readiness, completionContext);

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

        await InvokeAsync(request, client, readiness, context);

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

        await InvokeAsync(request, client, readiness, context);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, context.Response.StatusCode);
        Assert.False(readiness.FailureReported);
        Assert.Contains("tools_not_supported", await ReadBodyAsync(context));
    }

    private static async Task<InferenceJobStore> InvokeAsync(
        JsonElement request,
        StubInferenceClient client,
        StubReadiness readiness,
        DefaultHttpContext context,
        InferenceJobStore? jobs = null,
        NodeRegistry? nodes = null)
    {
        var jobStore = jobs ?? new InferenceJobStore(TimeProvider.System);
        await InferenceApiEndpoints.CompleteAsync(
            request,
            client,
            readiness,
            nodes ?? CreateReadyNodeRegistry(),
            new InferenceWorkloadScheduler(),
            jobStore,
            context);
        return jobStore;
    }

    private static NodeRegistry CreateReadyNodeRegistry()
    {
        var registry = new NodeRegistry(TimeProvider.System, TimeSpan.FromSeconds(6));
        for (var index = 0; index < 3; index++)
        {
            var nodeId = $"adb-test-{index}";
            registry.Register(new RegisterNodeRequest(nodeId, $"Test phone {index}", "Android test"), out _);
            registry.Heartbeat(nodeId, new HeartbeatRequest(10, 1000, 35, 5, RpcWorkerProcessState.Running), out _);
        }
        return registry;
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
            cancellationToken.ThrowIfCancellationRequested();
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
