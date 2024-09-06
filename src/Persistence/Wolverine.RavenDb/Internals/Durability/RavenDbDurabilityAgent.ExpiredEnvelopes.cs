using Microsoft.Extensions.Logging;
using Raven.Client.Documents.Operations;

namespace Wolverine.RavenDb.Internals.Durability;

public partial class RavenDbDurabilityAgent
{
    // TODO -- hopefully a temporary measure. Need to figure out how to 
    // set @expires metadata through a patch operation
    public async Task DeleteExpiredIncomingEnvelopes()
    {
        // TODO -- create index here?
        
        try
        {
            var time = DateTimeOffset.UtcNow;
            var query = new DeleteByQueryOperation<IncomingMessage>("IncomingMessages",
                x => x.Status == EnvelopeStatus.Handled && x.KeepUntil < time);

            var op = await _store.Operations.SendAsync(query);
            await op.WaitForCompletionAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to delete expired, handled messages");
        }
    }
}