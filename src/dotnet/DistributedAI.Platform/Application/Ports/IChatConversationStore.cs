using DistributedAI.Contracts;

namespace DistributedAI.Platform;

public interface IChatConversationStore
{
    Task<IReadOnlyList<ChatConversationSummary>> ListRecentAsync(CancellationToken cancellationToken);
    Task<ChatConversation?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ChatConversation> SaveAsync(
        Guid id,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken);
}
