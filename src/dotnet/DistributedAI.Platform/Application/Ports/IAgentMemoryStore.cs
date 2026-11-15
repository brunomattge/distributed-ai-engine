using DistributedAI.Contracts;

namespace DistributedAI.Platform;

public interface IAgentMemoryStore
{
    Task<IReadOnlyList<AgentMemory>> ListAsync(CancellationToken cancellationToken);
    Task<AgentMemory?> CreateAsync(string content, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
