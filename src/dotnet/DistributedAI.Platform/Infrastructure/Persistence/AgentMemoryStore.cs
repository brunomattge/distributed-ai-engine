using DistributedAI.Contracts;
using Npgsql;

namespace DistributedAI.Platform;

public sealed class PostgresAgentMemoryStore(NpgsqlDataSource dataSource) : IAgentMemoryStore
{
    public const int MaximumMemories = 10;

    public Task<IReadOnlyList<AgentMemory>> ListAsync(CancellationToken cancellationToken) =>
        PersistenceExceptionMapper.ExecuteAsync(() => ListCoreAsync(cancellationToken));

    private async Task<IReadOnlyList<AgentMemory>> ListCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT id, content, created_at, updated_at
            FROM agent_memories
            ORDER BY created_at DESC, id
            """,
            connection);

        var memories = new List<AgentMemory>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            memories.Add(new AgentMemory(
                reader.GetGuid(0),
                reader.GetString(1),
                Utc(reader.GetDateTime(2)),
                Utc(reader.GetDateTime(3))));
        }

        return memories;
    }

    public Task<AgentMemory?> CreateAsync(string content, CancellationToken cancellationToken) =>
        PersistenceExceptionMapper.ExecuteAsync(() => CreateCoreAsync(content, cancellationToken));

    private async Task<AgentMemory?> CreateCoreAsync(string content, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var advisoryLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@lock_key)",
            connection,
            transaction))
        {
            advisoryLock.Parameters.AddWithValue("lock_key", 94825731L);
            await advisoryLock.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var count = new NpgsqlCommand("SELECT COUNT(*) FROM agent_memories", connection, transaction))
        {
            var currentCount = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
            if (currentCount >= MaximumMemories)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        AgentMemory memory;
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO agent_memories (id, content)
            VALUES (@id, @content)
            RETURNING id, content, created_at, updated_at
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("content", content);
            await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            memory = new AgentMemory(
                reader.GetGuid(0),
                reader.GetString(1),
                Utc(reader.GetDateTime(2)),
                Utc(reader.GetDateTime(3)));
        }

        await transaction.CommitAsync(cancellationToken);
        return memory;
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        PersistenceExceptionMapper.ExecuteAsync(() => DeleteCoreAsync(id, cancellationToken));

    private async Task<bool> DeleteCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("DELETE FROM agent_memories WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
