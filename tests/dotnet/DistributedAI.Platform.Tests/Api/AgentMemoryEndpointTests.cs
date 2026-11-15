using DistributedAI.Contracts;
using DistributedAI.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace DistributedAI.Platform.Tests;

public sealed class AgentMemoryEndpointTests
{
    private static readonly IServiceProvider HttpServices = new ServiceCollection()
        .AddLogging()
        .AddRouting()
        .BuildServiceProvider();

    [Fact]
    public async Task CreateAsync_trims_and_stores_a_valid_memory()
    {
        var store = new FakeAgentMemoryStore();
        var result = await AgentMemoryEndpoints.CreateAsync(
            new SaveAgentMemoryRequest("  Prefers concise answers.  "),
            store,
            CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status201Created, status);
        Assert.Equal("Prefers concise answers.", store.Created!.Content);
        Assert.Contains(store.Created.Id.ToString(), body);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t\n")]
    public async Task CreateAsync_rejects_blank_memories(string content)
    {
        var store = new FakeAgentMemoryStore();
        var result = await AgentMemoryEndpoints.CreateAsync(
            new SaveAgentMemoryRequest(content), store, CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("invalid_memory", body);
        Assert.Null(store.Created);
    }

    [Fact]
    public async Task CreateAsync_rejects_content_over_the_limit()
    {
        var store = new FakeAgentMemoryStore();
        var result = await AgentMemoryEndpoints.CreateAsync(
            new SaveAgentMemoryRequest(new string('x', 1201)), store, CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("invalid_memory", body);
    }

    [Fact]
    public async Task CreateAsync_returns_conflict_when_the_memory_limit_is_reached()
    {
        var store = new FakeAgentMemoryStore { Full = true };
        var result = await AgentMemoryEndpoints.CreateAsync(
            new SaveAgentMemoryRequest("One more note"), store, CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Contains("memory_limit_reached", body);
    }

    [Fact]
    public async Task DeleteAsync_returns_not_found_for_unknown_memory()
    {
        var result = await AgentMemoryEndpoints.DeleteAsync(
            Guid.NewGuid(), new FakeAgentMemoryStore(), CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Contains("memory_not_found", body);
    }

    [Fact]
    public async Task ListAsync_maps_storage_failures_to_503()
    {
        var result = await AgentMemoryEndpoints.ListAsync(
            new FakeAgentMemoryStore { Failure = new StorageUnavailableException(new NpgsqlException("database unavailable")) },
            CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Contains("storage_unavailable", body);
    }

    private static async Task<(int StatusCode, string Body)> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext { RequestServices = HttpServices };
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        body.Position = 0;
        using var reader = new StreamReader(body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private sealed class FakeAgentMemoryStore : IAgentMemoryStore
    {
        private readonly List<AgentMemory> _items = [];
        public AgentMemory? Created { get; private set; }
        public bool Full { get; init; }
        public Exception? Failure { get; init; }

        public Task<IReadOnlyList<AgentMemory>> ListAsync(CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            return Task.FromResult<IReadOnlyList<AgentMemory>>(_items.ToArray());
        }

        public Task<AgentMemory?> CreateAsync(string content, CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            if (Full) return Task.FromResult<AgentMemory?>(null);
            var now = DateTimeOffset.UtcNow;
            Created = new AgentMemory(Guid.NewGuid(), content, now, now);
            _items.Add(Created);
            return Task.FromResult<AgentMemory?>(Created);
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            var item = _items.Find(memory => memory.Id == id);
            if (item is null) return Task.FromResult(false);
            _items.Remove(item);
            return Task.FromResult(true);
        }
    }
}

public sealed class PostgresAgentMemoryStoreTests
{
    [PostgresFact]
    public async Task Create_list_and_delete_memory_round_trip()
    {
        var connectionString = Environment.GetEnvironmentVariable("CHAT_DATABASE_URL")!;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgresAgentMemoryStore(dataSource);
        const string content = "PostgreSQL agent memory integration.";
        var created = await store.CreateAsync(content, CancellationToken.None);

        Assert.NotNull(created);
        try
        {
            var listed = await store.ListAsync(CancellationToken.None);
            Assert.Contains(listed, memory => memory.Id == created.Id && memory.Content == content);
            Assert.True(await store.DeleteAsync(created.Id, CancellationToken.None));
            Assert.DoesNotContain(await store.ListAsync(CancellationToken.None), memory => memory.Id == created.Id);
        }
        finally
        {
            await store.DeleteAsync(created.Id, CancellationToken.None);
        }
    }
}
