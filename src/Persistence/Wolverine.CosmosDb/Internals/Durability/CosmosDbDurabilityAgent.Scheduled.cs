using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace Wolverine.CosmosDb.Internals.Durability;

public partial class CosmosDbDurabilityAgent
{
    private async Task runScheduledJobs()
    {
        try
        {
            if (!(await _parent.TryAttainScheduledJobLockAsync(_combined.Token)))
            {
                return;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to attain the scheduled job lock");
            return;
        }

        try
        {
            var queryText =
                "SELECT * FROM c WHERE c.docType = @docType AND c.status = @status AND c.executionTime <= @now ORDER BY c.executionTime OFFSET 0 LIMIT @limit";
            var query = new QueryDefinition(queryText)
                .WithParameter("@docType", DocumentTypes.Incoming)
                .WithParameter("@status", EnvelopeStatus.Scheduled)
                .WithParameter("@now", DateTimeOffset.UtcNow)
                .WithParameter("@limit", _settings.RecoveryBatchSize);

            var incoming = new List<IncomingMessage>();
            using var iterator = _container.GetItemQueryIterator<IncomingMessage>(query);

            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(_combined.Token);
                incoming.AddRange(response);
            }

            if (!incoming.Any())
            {
                return;
            }

            await locallyPublishScheduledMessages(incoming);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while trying to process scheduled messages");
        }
        finally
        {
            try
            {
                await _parent.ReleaseScheduledJobLockAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to release the scheduled job lock");
            }
        }
    }

    private async Task locallyPublishScheduledMessages(List<IncomingMessage> incoming)
    {
        var envelopes = incoming.Select(x => x.Read()).ToList();

        foreach (var message in incoming)
        {
            message.Status = EnvelopeStatus.Incoming;
            message.OwnerId = _settings.AssignedNodeNumber;
            await _container.ReplaceItemAsync(message, message.Id,
                new PartitionKey(message.PartitionKey));
        }

        foreach (var envelope in envelopes)
        {
            _logger.LogInformation("Locally enqueuing scheduled message {Id} of type {MessageType}", envelope.Id,
                envelope.MessageType);
            await _localQueue.EnqueueAsync(envelope);
        }
    }
}
