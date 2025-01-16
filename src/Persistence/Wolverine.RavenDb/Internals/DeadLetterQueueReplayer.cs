using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.DataArchival;
using Raven.Client.Documents.Subscriptions;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;

namespace Wolverine.RavenDb.Internals;

public class DeadLetterQueueReplayer : IHostedService
{
    private readonly IDocumentStore _store;
    private readonly IWolverineRuntime _runtime;
    private Task? _subscriptionTask;
    private readonly ILogger<DeadLetterQueueReplayer> _logger;
    private readonly RavenDbMessageStore _messageStore;

    public DeadLetterQueueReplayer(IDocumentStore store, IWolverineRuntime runtime)
    {
        _store = store;
        _runtime = runtime;
        _logger = _runtime.LoggerFactory.CreateLogger<DeadLetterQueueReplayer>();
        _messageStore = new RavenDbMessageStore(_store, runtime.Options);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = new SubscriptionCreationOptions
        {
            ArchivedDataProcessingBehavior = ArchivedDataProcessingBehavior.ExcludeArchived,
            
        };
        
        string subscriptionName = await _store.Subscriptions.CreateAsync<DeadLetterMessage>(x => x.Replayable, options, token: _runtime.Cancellation);
        var subscription = _store.Subscriptions.GetSubscriptionWorker<DeadLetterMessage>(subscriptionName);

        _subscriptionTask = subscription.Run(async batch =>
        {
            await processBatchAsync(batch);
        }, _runtime.Cancellation);
    }

    private async Task processBatchAsync(SubscriptionBatch<DeadLetterMessage> batch)
    {
        try
        {
            using var session = batch.OpenAsyncSession();
        
            foreach (var item in batch.Items)
            {
                var deadLetterMessage = item.Result;
                var envelope = EnvelopeSerializer.Deserialize(deadLetterMessage.Body);
                envelope.Status = EnvelopeStatus.Incoming;
                var incoming = new IncomingMessage(envelope, _messageStore)
                {
                    OwnerId = 0,
                    Id = _messageStore.IdentityFor(envelope)
                };

                await session.StoreAsync(incoming);
                session.Delete(deadLetterMessage);
            }

            await session.SaveChangesAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to move replayable dead letter queue messages to incoming");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscriptionTask.SafeDispose();
        return Task.CompletedTask;
    }
}