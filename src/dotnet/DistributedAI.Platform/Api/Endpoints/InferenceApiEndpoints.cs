using System.Text;
using System.Text.Json;
using DistributedAI.Contracts;

namespace DistributedAI.Platform;

public static class InferenceApiEndpoints
{
    public static RouteHandlerBuilder MapInferenceApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/models", ListModelsAsync);
        return endpoints.MapPost("/v1/chat/completions", CompleteAsync);
    }

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

        if (stream)
        {
            await StreamAsync(request, inference, readiness, context);
            return;
        }

        try
        {
            var completion = await inference.CompleteAsync(request, context.RequestAborted);
            await Results.Json(completion).ExecuteAsync(context);
        }
        catch (PythonInferenceException exception)
        {
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

    private static async Task StreamAsync(
        JsonElement request,
        IPythonInferenceClient inference,
        IInferenceReadiness readiness,
        HttpContext context)
    {
        await using var enumerator = inference.StreamCompletionAsync(request, context.RequestAborted)
            .GetAsyncEnumerator(context.RequestAborted);

        ReadOnlyMemory<byte> firstChunk;
        try
        {
            if (!await enumerator.MoveNextAsync())
            {
                readiness.ReportSessionFailure();
                await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "engine_error", "The inference runtime returned an empty stream.", "inference_degraded");
                return;
            }

            firstChunk = enumerator.Current;
        }
        catch (PythonInferenceException exception)
        {
            if (IsClientError(exception.StatusCode))
            {
                await ExecuteUpstreamErrorAsync(context, exception);
                return;
            }

            readiness.ReportSessionFailure();
            await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "engine_error", "Distributed inference failed. The session is degraded and recovering.", "inference_degraded");
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
            if (!errorDetector.HasDone)
            {
                readiness.ReportSessionFailure();
                if (errorDetector.HasError)
                {
                    await WriteSseDoneAsync(context);
                }
                else
                {
                    await WriteSseErrorAsync(context, "The inference stream ended before its completion marker.");
                }
            }
        }
        catch (PythonInferenceException)
        {
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
