using Wolverine.Persistence.Durability;

namespace Wolverine.RavenDb.Internals;

public partial class RavenDbMessageStore : IDeadLetters
{
    public async Task<DeadLetterEnvelopesFound> QueryDeadLetterEnvelopesAsync(DeadLetterEnvelopeQueryParameters queryParameters, string? tenantId = null)
    {
        throw new NotImplementedException();
    }

    public async Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null)
    {
        throw new NotImplementedException();
    }

    public async Task<int> MarkDeadLetterEnvelopesAsReplayableAsync(string exceptionType = "")
    {
        throw new NotImplementedException();
    }

    public async Task MarkDeadLetterEnvelopesAsReplayableAsync(Guid[] ids, string? tenantId = null)
    {
        throw new NotImplementedException();
    }

    public async Task DeleteDeadLetterEnvelopesAsync(Guid[] ids, string? tenantId = null)
    {
        throw new NotImplementedException();
    }
}