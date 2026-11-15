using DistributedAI.Contracts;
using Npgsql;

namespace DistributedAI.Platform;

public sealed class PostgresChatConversationStore(NpgsqlDataSource dataSource) : IChatConversationStore
{
    public Task<IReadOnlyList<ChatConversationSummary>> ListRecentAsync(CancellationToken cancellationToken) =>
        PersistenceExceptionMapper.ExecuteAsync(() => ListRecentCoreAsync(cancellationToken));

    private async Task<IReadOnlyList<ChatConversationSummary>> ListRecentCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT c.id, c.updated_at,
                   COALESCE((
                       SELECT LEFT(m.content, 120)
                       FROM chat_messages AS m
                       WHERE m.conversation_id = c.id AND m.role = 'user'
                       ORDER BY m.ordinal
                       LIMIT 1
                   ), 'New conversation') AS title
            FROM chat_conversations AS c
            ORDER BY c.updated_at DESC
            LIMIT 50
            """,
            connection);

        var summaries = new List<ChatConversationSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var updatedAt = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
            summaries.Add(new ChatConversationSummary(
                reader.GetGuid(0),
                reader.GetString(2),
                new DateTimeOffset(updatedAt)));
        }

        return summaries;
    }

    public Task<ChatConversation?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        PersistenceExceptionMapper.ExecuteAsync(() => GetCoreAsync(id, cancellationToken));

    private async Task<ChatConversation?> GetCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var conversationCommand = new NpgsqlCommand(
            """
            SELECT created_at, updated_at
            FROM chat_conversations
            WHERE id = @id
            """,
            connection);
        conversationCommand.Parameters.AddWithValue("id", id);

        DateTime createdAt;
        DateTime updatedAt;
        await using (var reader = await conversationCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            createdAt = reader.GetDateTime(0);
            updatedAt = reader.GetDateTime(1);
        }

        var messages = new List<ChatMessage>();
        await using var messagesCommand = new NpgsqlCommand(
            """
            SELECT role, content
            FROM chat_messages
            WHERE conversation_id = @id
            ORDER BY ordinal
            """,
            connection);
        messagesCommand.Parameters.AddWithValue("id", id);
        await using (var reader = await messagesCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                messages.Add(new ChatMessage(reader.GetString(0), reader.GetString(1)));
            }
        }

        return new ChatConversation(
            id,
            messages,
            new DateTimeOffset(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(updatedAt, DateTimeKind.Utc)));
    }

    public Task<ChatConversation> SaveAsync(
        Guid id,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken) =>
        PersistenceExceptionMapper.ExecuteAsync(() => SaveCoreAsync(id, messages, cancellationToken));

    private async Task<ChatConversation> SaveCoreAsync(
        Guid id,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var upsert = new NpgsqlCommand(
            """
            INSERT INTO chat_conversations (id)
            VALUES (@id)
            ON CONFLICT (id) DO UPDATE SET updated_at = now()
            """,
            connection,
            transaction))
        {
            upsert.Parameters.AddWithValue("id", id);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var delete = new NpgsqlCommand(
            "DELETE FROM chat_messages WHERE conversation_id = @id",
            connection,
            transaction))
        {
            delete.Parameters.AddWithValue("id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var ordinal = 0; ordinal < messages.Count; ordinal++)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO chat_messages (conversation_id, ordinal, role, content)
                VALUES (@id, @ordinal, @role, @content)
                """,
                connection,
                transaction);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("ordinal", ordinal);
            insert.Parameters.AddWithValue("role", messages[ordinal].Role);
            insert.Parameters.AddWithValue("content", messages[ordinal].Content);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (await GetAsync(id, cancellationToken))!;
    }
}
