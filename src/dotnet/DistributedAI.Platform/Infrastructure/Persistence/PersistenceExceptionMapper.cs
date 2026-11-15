using Npgsql;

namespace DistributedAI.Platform;

internal static class PersistenceExceptionMapper
{
    public static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (NpgsqlException exception)
        {
            throw new StorageUnavailableException(exception);
        }
    }
}
