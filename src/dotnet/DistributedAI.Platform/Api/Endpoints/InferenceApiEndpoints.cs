using System.Text;
using System.Text.Json;
using DistributedAI.Contracts;

namespace DistributedAI.Platform;

public static class InferenceApiEndpoints
{
    public static RouteHandlerBuilder MapInferenceApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/models", ListModelsAsync);
        endpoints.MapGet("/api/inference/jobs/{jobId:guid}", GetJobAsync);
        return endpoints.MapPost("/v1/chat/completions", CompleteAsync);
    }

    public static IResult GetJobAsync(Guid jobId, IInferenceJobStore jobs) =>
        jobs.Get(jobId) is { } job ? Results.Ok(job) : Results.NotFound();

    public static async Task<IResult> ListModelsAsync(
        IPythonInferenceClient inference,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Json(await inference.ListModelsAsync(cancellationToken));
        }
        catch (PythonInferenceException exception)
        {
            return UpstreamError(exception);
        }
    }

    public static async Task CompleteAsync(
        JsonElement request,
        IPythonInferenceClient inference,
        IInferenceReadiness readiness,
        NodeRegistry nodes,
        InferenceWorkloadScheduler scheduler,
        IInferenceJobStore jobs,
        HttpContext context)
    {
        if (request.ValueKind != JsonValueKind.Object)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request_error", "The request body must be a JSON object.");
            return;
        }

        var stream = false;
        if (request.TryGetProperty("stream", out var streamValue))
        {
            if (streamValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request_error", "stream must be a boolean.");
                return;
            }

            stream = streamValue.GetBoolean();
        }

        var requestedMode = ReadWorkloadMode(request);
        if (requestedMode.Error is not null)
        {
            await WriteErrorAsync(
                context,
                requestedMode.IsUnsupported ? StatusCodes.Status501NotImplemented : StatusCodes.Status400BadRequest,
                requestedMode.IsUnsupported ? "unsupported_workload" : "invalid_request_error",
                requestedMode.Error,
                requestedMode.IsUnsupported ? "workload_mode_unsupported" : "invalid_workload_mode");
            return;
        }

        var status = await readiness.GetAsync(context.RequestAborted);
        if (!status.Available)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "service_unavailable",
                status.Message ?? "Distributed inference is unavailable.",
                "inference_unavailable");
            return;
        }

        if (!request.TryGetProperty("model", out var modelValue) || modelValue.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(modelValue.GetString()))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request_error", "model must identify the loaded engine model.", "invalid_model");
            return;
        }

        if (!string.Equals(modelValue.GetString(), status.ModelId, StringComparison.Ordinal))
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "invalid_request_error", "The requested model is not loaded by this cluster.", "model_not_found");
            return;
        }

        var requirement = new InferencePlacementRequirement(status.ModelId, InferenceWorkloadScheduler.RpcBackend, status.RequiredWorkers);
        var capabilities = InferenceWorkloadScheduler.FromCurrentCluster(nodes.GetAll(), status.ModelId);
        var placement = scheduler.PlanSingleModel(requirement, capabilities);
        if (placement is null)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "service_unavailable",
                "No eligible worker placement satisfies the loaded model and backend requirements.",
                "placement_unavailable");
            return;
        }

        InferenceJob job;
        try
        {
            job = jobs.CreateAccepted(placement);
        }
        catch (InferenceJobCapacityException)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "service_unavailable",
                "The local inference job registry is full. Retry after active requests finish.",
                "job_capacity_reached");
            return;
        }
        SetJobHeaders(context.Response, job);
        jobs.TryStart(job.Id);

        if (stream)
        {
            await StreamAsync(request, inference, readiness, jobs, job, context);
            return;
        }

        try
        {
            var completion = await inference.CompleteAsync(request, context.RequestAborted);
            jobs.TryFinish(job.Id, InferenceJobStates.Completed);
            await Results.Json(completion).ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            jobs.TryFinish(job.Id, InferenceJobStates.Cancelled, "client_cancelled");
        }
        catch (PythonInferenceException exception)
        {
            jobs.TryFinish(
                job.Id,
                exception.IsTimeout ? InferenceJobStates.TimedOut : InferenceJobStates.Failed,
                exception.IsTimeout ? "runtime_timeout" : IsClientError(exception.StatusCode) ? "request_rejected" : "runtime_error");
            if (IsClientError(exception.StatusCode))
            {
                await ExecuteUpstreamErrorAsync(context, exception);
                return;
            }

            readiness.ReportSessionFailure();
            await WriteErrorAsync(
                context,
                StatusCodes.Status502BadGateway,
                "engine_error",
                "Distributed inference failed. The session is degraded and recovering.",
                "inference_degraded");
        }
    }

    private static (string? Error, bool IsUnsupported) ReadWorkloadMode(JsonElement request)
    {
        if (!request.TryGetProperty("workload_mode", out var value)) return (null, false);
        if (value.ValueKind != JsonValueKind.String) return ("workload_mode must be a string.", false);

        return value.GetString() switch
        {
            InferenceWorkloadModes.SingleModelDistributed => (null, false),
            InferenceWorkloadModes.IndependentReplicas =>
                ("Independent replicas are not executed by this endpoint. Each accepted completion is one single-model distributed job.", true),
            _ => ("workload_mode must be single_model_distributed.", false),
        };
    }

    private static void SetJobHeaders(HttpResponse response, InferenceJob job)
    {
        response.Headers["X-Distributed-AI-Job-Id"] = job.Id.ToString("D");
        response.Headers["X-Distributed-AI-Workload-Mode"] = job.WorkloadMode;
        response.Headers["X-Distributed-AI-Model"] = job.ModelId;
        response.Headers["X-Distributed-AI-Backend"] = job.Backend;
        response.Headers["X-Distributed-AI-Selected-Nodes"] = string.Join(',', job.SelectedNodeIds);
    }

    private static async Task StreamAsync(
        JsonElement request,
        IPythonInferenceClient inference,
        IInferenceReadiness readiness,
        IInferenceJobStore jobs,
        InferenceJob job,
        HttpContext context)
    {
        await using var enumerator = inference.StreamCompletionAsync(request, context.RequestAborted)
            .GetAsyncEnumerator(context.RequestAborted);

        ReadOnlyMemory<byte> firstChunk;
        try
        {
            if (!await enumerator.MoveNextAsync())
            {
                jobs.TryFinish(job.Id, InferenceJobStates.Failed, "empty_stream");
                readiness.ReportSessionFailure();
                await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "engine_error", "The inference runtime returned an empty stream.", "inference_degraded");
                return;
            }

            firstChunk = enumerator.Current;
        }
        catch (PythonInferenceException exception)
        {
            jobs.TryFinish(
                job.Id,
                exception.IsTimeout ? InferenceJobStates.TimedOut : InferenceJobStates.Failed,
                exception.IsTimeout ? "runtime_timeout" : IsClientError(exception.StatusCode) ? "request_rejected" : "runtime_error");
            if (IsClientError(exception.StatusCode))
            {
                await ExecuteUpstreamErrorAsync(context, exception);
                return;
            }

            readiness.ReportSessionFailure();
            await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "engine_error", "Distributed inference failed. The session is degraded and recovering.", "inference_degraded");
            return;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            jobs.TryFinish(job.Id, InferenceJobStates.Cancelled, "client_cancelled");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        var errorDetector = new SseErrorDetector();
        await WriteChunkAsync(firstChunk, context, errorDetector, readiness);
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                await WriteChunkAsync(enumerator.Current, context, errorDetector, readiness);
            }
            if (errorDetector.HasError)
            {
                jobs.TryFinish(job.Id, InferenceJobStates.Failed, "upstream_error");
                if (!errorDetector.HasDone) await WriteSseDoneAsync(context);
            }
            else if (!errorDetector.HasDone)
            {
                jobs.TryFinish(job.Id, InferenceJobStates.Failed, "missing_completion_marker");
                readiness.ReportSessionFailure();
                await WriteSseErrorAsync(context, "The inference stream ended before its completion marker.");
            }
            else
            {
                jobs.TryFinish(job.Id, InferenceJobStates.Completed);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            jobs.TryFinish(job.Id, InferenceJobStates.Cancelled, "client_cancelled");
        }
        catch (PythonInferenceException exception)
        {
            jobs.TryFinish(
                job.Id,
                exception.IsTimeout ? InferenceJobStates.TimedOut : InferenceJobStates.Failed,
                exception.IsTimeout ? "runtime_timeout" : "runtime_error");
            readiness.ReportSessionFailure();
            if (errorDetector.HasError)
            {
                await WriteSseDoneAsync(context);
            }
            else
            {
                await WriteSseErrorAsync(context, "Distributed inference failed. The session is degraded and recovering.");
            }
        }
    }

    private static async Task WriteChunkAsync(
        ReadOnlyMemory<byte> chunk,
        HttpContext context,
        SseErrorDetector errorDetector,
        IInferenceReadiness readiness)
    {
        if (errorDetector.ContainsError(chunk)) readiness.ReportSessionFailure();
        await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static async Task ExecuteUpstreamErrorAsync(HttpContext context, PythonInferenceException exception)
    {
        if (exception.ResponseBody is not null)
        {
            context.Response.StatusCode = exception.StatusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(exception.ResponseBody, context.RequestAborted);
            return;
        }

        await WriteErrorAsync(context, exception.StatusCode, "invalid_request_error", exception.Message);
    }

    private static IResult UpstreamError(PythonInferenceException exception) =>
        exception.ResponseBody is not null
            ? Results.Content(exception.ResponseBody, "application/json; charset=utf-8", statusCode: exception.StatusCode)
            : Results.Json(new { error = new { message = exception.Message, type = "engine_error", code = "runtime_unavailable" } }, statusCode: exception.StatusCode);

    private static bool IsClientError(int statusCode) => statusCode is >= 400 and < 500;

    private static Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string type,
        string message,
        string code = "invalid_request") =>
        Results.Json(new { error = new { message, type, code } }, statusCode: statusCode).ExecuteAsync(context);

    private static async Task WriteSseErrorAsync(HttpContext context, string message)
    {
        var error = JsonSerializer.Serialize(new { error = new { message, type = "engine_error", code = "inference_degraded" } });
        await context.Response.WriteAsync($"data: {error}\n\ndata: [DONE]\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static async Task WriteSseDoneAsync(HttpContext context)
    {
        await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private sealed class SseErrorDetector
    {
        private readonly StringBuilder _pending = new();

        public bool HasError { get; private set; }
        public bool HasDone { get; private set; }

        public bool ContainsError(ReadOnlyMemory<byte> chunk)
        {
            _pending.Append(Encoding.UTF8.GetString(chunk.Span));
            var content = _pending.ToString();
            var found = false;
            var separator = content.IndexOf("\n\n", StringComparison.Ordinal);
            while (separator >= 0)
            {
                var eventText = content[..separator];
                foreach (var line in eventText.Split('\n'))
                {
                    var normalized = line.TrimEnd('\r');
                    if (!normalized.StartsWith("data:", StringComparison.Ordinal)) continue;

                    var data = normalized[5..].Trim();
                    if (data == "[DONE]")
                    {
                        HasDone = true;
                        continue;
                    }

                    try
                    {
                        using var document = JsonDocument.Parse(data);
                        if (document.RootElement.ValueKind == JsonValueKind.Object &&
                            document.RootElement.TryGetProperty("error", out _))
                        {
                            found = true;
                            HasError = true;
                        }
                    }
                    catch (JsonException)
                    {
                        // Ignore non-JSON SSE payloads; only structured error events degrade the session.
                    }
                }

                content = content[(separator + 2)..];
                separator = content.IndexOf("\n\n", StringComparison.Ordinal);
            }

            _pending.Clear();
            _pending.Append(content);
            return found;
        }
    }
}
