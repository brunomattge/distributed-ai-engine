namespace DistributedAI.Platform;

public sealed class StorageUnavailableException(Exception innerException)
    : Exception("Persistent storage is unavailable.", innerException);
