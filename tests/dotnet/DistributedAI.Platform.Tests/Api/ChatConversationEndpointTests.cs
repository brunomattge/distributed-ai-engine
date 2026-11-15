using DistributedAI.Contracts;
using DistributedAI.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace DistributedAI.Platform.Tests;

public sealed class ChatConversationEndpointTests
{
    private static readonly IServiceProvider HttpServices = new ServiceCollection()
        .AddLogging()
        .AddRouting()
        .BuildServiceProvider();

    [Fact]
    public async Task ListRecentAsync_returns_saved_conversation_summaries()
    {
        var id = Guid.NewGuid();
        var store = new FakeConversationStore();
        await store.SaveAsync(
            id,
            [new ChatMessage("user", "Test summary"), new ChatMessage("assistant", "Response")],
            CancellationToken.None);

        var result = await ChatConversationEndpoints.ListRecentAsync(store, CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains(id.ToString(), body);
        Assert.Contains("Test summary", body);
    }

    [Fact]
    public async Task ListRecentAsync_maps_postgres_unavailability_to_503()
    {
        var store = new FakeConversationStore { Failure = new StorageUnavailableException(new NpgsqlException("database unavailable")) };

        var result = await ChatConversationEndpoints.ListRecentAsync(store, CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Contains("storage_unavailable", body);
    }

    [Fact]
    public async Task SaveAsync_stores_valid_user_and_assistant_messages()
    {
        var id = Guid.NewGuid();
        var messages = new[] { new ChatMessage("user", "Hi"), new ChatMessage("assistant", "Hello.") };
        var store = new FakeConversationStore();

        var result = await ChatConversationEndpoints.SaveAsync(
            id, new SaveChatConversationRequest(messages), store, CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(id, store.Saved!.Id);
        Assert.Equal(messages, store.Saved.Messages);
        Assert.Contains("\"createdAtUtc\"", body);
    }

    [Fact]
    public async Task SaveAsync_rejects_tool_messages_and_does_not_call_store()
    {
        var store = new FakeConversationStore();

        var result = await ChatConversationEndpoints.SaveAsync(
            Guid.NewGuid(),
            new SaveChatConversationRequest([new ChatMessage("tool", "result")]),
            store,
            CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("invalid_conversation", body);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task GetAsync_returns_not_found_for_missing_conversation()
    {
        var result = await ChatConversationEndpoints.GetAsync(
            Guid.NewGuid(), new FakeConversationStore(), CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Contains("conversation_not_found", body);
    }

    [Fact]
    public async Task SaveAsync_maps_postgres_unavailability_to_503()
    {
        var store = new FakeConversationStore { Failure = new StorageUnavailableException(new NpgsqlException("database unavailable")) };

        var result = await ChatConversationEndpoints.SaveAsync(
            Guid.NewGuid(),
            new SaveChatConversationRequest([new ChatMessage("user", "Hi")]),
            store,
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

    private sealed class FakeConversationStore : IChatConversationStore
    {
        public ChatConversation? Saved { get; private set; }
        public Exception? Failure { get; init; }

        public Task<IReadOnlyList<ChatConversationSummary>> ListRecentAsync(CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            IReadOnlyList<ChatConversationSummary> summaries = Saved is null
                ? []
                : [new ChatConversationSummary(Saved.Id, Saved.Messages.First().Content, Saved.UpdatedAtUtc)];
            return Task.FromResult(summaries);
        }

        public Task<ChatConversation?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Saved?.Id == id ? Saved : null);

        public Task<ChatConversation> SaveAsync(
            Guid id,
            IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            var now = DateTimeOffset.UtcNow;
            Saved = new ChatConversation(id, messages, now, now);
            return Task.FromResult(Saved);
        }
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CHAT_DATABASE_URL")))
        {
            Skip = "Set CHAT_DATABASE_URL to run the PostgreSQL integration test.";
        }
    }
}

public sealed class PostgresChatConversationStoreTests
{
    [PostgresFact]
    public async Task Save_then_get_restores_messages_in_order()
    {
        var connectionString = Environment.GetEnvironmentVariable("CHAT_DATABASE_URL")!;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgresChatConversationStore(dataSource);
        var id = Guid.NewGuid();

        try
        {
            var original = new[]
            {
                new ChatMessage("user", "Hello, banco"),
                new ChatMessage("assistant", "Persisted history."),
            };
            var saved = await store.SaveAsync(id, original, CancellationToken.None);

            Assert.Equal(id, saved.Id);
            Assert.Equal(original, saved.Messages);

            var updated = original.Concat(new[]
            {
                new ChatMessage("user", "Consigo retomar?"),
                new ChatMessage("assistant", "Sim."),
            }).ToArray();
            await store.SaveAsync(id, updated, CancellationToken.None);
            var restored = await store.GetAsync(id, CancellationToken.None);

            Assert.NotNull(restored);
            Assert.Equal(updated, restored.Messages);
            Assert.True(restored.UpdatedAtUtc >= saved.UpdatedAtUtc);
            var recent = await store.ListRecentAsync(CancellationToken.None);
            Assert.Contains(recent, item => item.Id == id && item.Title == "Hello, banco");
        }
        finally
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var cleanup = new NpgsqlCommand(
                "DELETE FROM chat_conversations WHERE id = @id",
                connection);
            cleanup.Parameters.AddWithValue("id", id);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
