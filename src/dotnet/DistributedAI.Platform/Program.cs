using System.Text.Json.Serialization;
using DistributedAI.Contracts;
using DistributedAI.Platform;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var listenUrl = Environment.GetEnvironmentVariable("PLATFORM_URLS") ?? "http://127.0.0.1:5100";
var agentRuntimeUrl = Environment.GetEnvironmentVariable("AGENT_RUNTIME_URL") ?? "http://127.0.0.1:8000/";
var chatDatabaseUrl = Environment.GetEnvironmentVariable("CHAT_DATABASE_URL")
    ?? "Host=127.0.0.1;Port=5432;Database=distributed_ai;Username=distributed_ai;Timeout=5;Command Timeout=30";
builder.WebHost.UseUrls(listenUrl);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(provider =>
    new NodeRegistry(provider.GetRequiredService<TimeProvider>(), TimeSpan.FromSeconds(6)));
builder.Services.AddHttpClient<IPythonAgentClient, PythonAgentClient>(client =>
{
    client.BaseAddress = new Uri(agentRuntimeUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromMinutes(20);
});
builder.Services.AddSingleton<IInferenceReadiness, InferenceReadinessService>();
builder.Services.AddSingleton(NpgsqlDataSource.Create(chatDatabaseUrl));
builder.Services.AddSingleton<IChatConversationStore, PostgresChatConversationStore>();
builder.Services.AddSingleton<IAgentMemoryStore, PostgresAgentMemoryStore>();

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/model-status", async (IPythonAgentClient agent, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await agent.GetHealthAsync(cancellationToken));
    }
    catch (PythonAgentUnavailableException)
    {
        return Results.Ok(new { api = "unavailable", state = "unavailable", model_id = "none", model_name = "No model loaded" });
    }
});

app.MapGet("/api/inference-status", (IInferenceReadiness readiness, CancellationToken cancellationToken) =>
    readiness.GetAsync(cancellationToken));

app.MapChatEndpoint();
app.MapChatStreamEndpoint();
app.MapChatConversationEndpoints();
app.MapAgentMemoryEndpoints();

app.MapGet("/api/nodes", (NodeRegistry registry) => Results.Ok(registry.GetAll()));

app.MapPost("/api/nodes", (RegisterNodeRequest request, NodeRegistry registry) =>
{
    var result = registry.Register(request, out var node);
    return result switch
    {
        RegistrationResult.Created => Results.Created($"/api/nodes/{Uri.EscapeDataString(node!.NodeId)}", node),
        RegistrationResult.Conflict => Results.Conflict(new ApiError("node_exists", "A node with this ID is already registered.")),
        _ => Results.BadRequest(new ApiError("invalid_node", "Node ID, name, or hardware model is missing or too long."))
    };
});

app.MapPost("/api/nodes/{nodeId}/heartbeat", (string nodeId, HeartbeatRequest request, NodeRegistry registry) =>
{
    var result = registry.Heartbeat(nodeId, request, out var node);
    return result switch
    {
        HeartbeatResult.Accepted => Results.Ok(node),
        HeartbeatResult.NotFound => Results.NotFound(new ApiError("node_not_found", "Register the node before sending heartbeats.")),
        _ => Results.BadRequest(new ApiError("invalid_heartbeat", "Telemetry values are outside the accepted ranges."))
    };
});

app.MapPost("/api/nodes/{nodeId}/disconnect", (string nodeId, NodeRegistry registry) =>
{
    var result = registry.MarkDisconnected(nodeId, out var node);
    return result switch
    {
        DisconnectResult.Disconnected => Results.Ok(node),
        _ => Results.NotFound(new ApiError("node_not_found", "The node is not registered."))
    };
});

app.Run();

public partial class Program { }
