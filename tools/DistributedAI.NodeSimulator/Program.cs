using System.Net.Http.Json;
using DistributedAI.Contracts;

var apiBase = args.ElementAtOrDefault(0) ?? "http://127.0.0.1:5100";
if (!Uri.TryCreate(apiBase, UriKind.Absolute, out var baseUri) ||
    baseUri.Scheme is not ("http" or "https"))
{
    Console.Error.WriteLine("Usage: DistributedAI.NodeSimulator [http://127.0.0.1:5100]");
    return 2;
}

using var client = new HttpClient { BaseAddress = baseUri };
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stop.Cancel();
};

var random = new Random();
var nodes = Enumerable.Range(1, 3)
    .Select(index => new RegisterNodeRequest(
        $"sim-a07-{index}-{Guid.NewGuid():N}",
        $"Galaxy A07 simulado {index}",
        "SM-A075M (simulado)"))
    .ToArray();

try
{
    foreach (var node in nodes)
    {
        using var response = await client.PostAsJsonAsync("/api/nodes", node, stop.Token);
        response.EnsureSuccessStatusCode();
        Console.WriteLine($"Registered simulated node {node.NodeId}: {node.Name}");
    }

    Console.WriteLine("Sending simulated heartbeats every 2 seconds. Press Ctrl+C to stop.");
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
    do
    {
        foreach (var node in nodes)
        {
            var heartbeat = new HeartbeatRequest(
                Math.Round(15 + random.NextDouble() * 60, 1),
                random.Next(450, 1_250),
                Math.Round(32 + random.NextDouble() * 12, 1),
                Math.Round(1 + random.NextDouble() * 12, 1));
            using var response = await client.PostAsJsonAsync(
                $"/api/nodes/{Uri.EscapeDataString(node.NodeId)}/heartbeat",
                heartbeat,
                stop.Token);
            response.EnsureSuccessStatusCode();
            Console.WriteLine($"Heartbeat {node.NodeId}: CPU {heartbeat.CpuPercent}% | RAM {heartbeat.AvailableRamMiB} MiB | {heartbeat.TemperatureCelsius} °C");
        }
    } while (await timer.WaitForNextTickAsync(stop.Token));
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    Console.WriteLine("Simulator stopped. Nodes will be reported offline after the 6-second heartbeat timeout.");
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Simulator failed: {exception.Message}");
    return 1;
}

return 0;
