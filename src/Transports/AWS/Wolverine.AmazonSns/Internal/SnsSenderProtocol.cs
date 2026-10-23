using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSns.Internal;

internal class SnsSenderProtocol :ISenderProtocol
{
    private readonly ILogger _logger;
    private readonly AmazonSnsTopic _topic;
    private readonly IAmazonSimpleNotificationService _sns;

    public SnsSenderProtocol(IWolverineRuntime runtime, AmazonSnsTopic topic, IAmazonSimpleNotificationService sns)
    {
        _topic = topic;
        _sns = sns;
        _logger = runtime.LoggerFactory.CreateLogger<SnsSenderProtocol>();
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        await _topic.InitializeAsync(_logger);

        // PublishBatch takes at most 10 entries, whatever MessageBatchSize the endpoint was given
        if (batch.Messages.Count <= MaximumBatchEntries)
        {
            await sendChunkAsync(callback, batch);
            return;
        }

        foreach (var chunk in batch.Messages.Chunk(MaximumBatchEntries))
        {
            await sendChunkAsync(callback, new OutgoingMessageBatch(batch.Destination, chunk));
        }
    }

    /// <summary>
    ///     The most entries one SNS PublishBatch request accepts
    /// </summary>
    internal const int MaximumBatchEntries = 10;

    private async Task sendChunkAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        var snsBatch = new OutgoingSnsBatch(_topic, _logger, batch.Messages);

        // GH-5055. Mapping it again can never succeed, so it is dead lettered rather than retried, and it is not
        // reported as sent with the rest, which it used to be
        if (snsBatch.MappingFailures.Count > 0)
        {
            await callback.MarkSerializationFailureAsync(
                new OutgoingMessageBatch(batch.Destination, snsBatch.MappingFailures));

            // PublishBatch refuses a request without entries
            if (snsBatch.Request.PublishBatchRequestEntries.Count == 0) return;

            batch = new OutgoingMessageBatch(batch.Destination,
                batch.Messages.Where(x => !snsBatch.MappingFailures.Contains(x)).ToList());
        }

        try
        {
            var response = await _sns.PublishBatchAsync(snsBatch.Request);

            await snsBatch.ProcessSuccessAsync(callback, response, batch);
        }
        catch (Exception e)
        {
            await callback.MarkProcessingFailureAsync(batch, e);
        }
    }
}

internal class OutgoingSnsBatch
{
    private readonly Dictionary<string, Envelope> _envelopes = new();
    private readonly List<Envelope> _mappingFailures = new();

    public PublishBatchRequest Request { get; }

    /// <summary>
    ///     Envelopes the topic's mapper threw on. They are not in the request, and can never be sent
    /// </summary>
    public IReadOnlyList<Envelope> MappingFailures => _mappingFailures;

    public OutgoingSnsBatch(AmazonSnsTopic topic, ILogger logger, IEnumerable<Envelope> envelopes)
    {
        var entries = new List<PublishBatchRequestEntry>();
        foreach (var envelope in envelopes)
        {
            try
            {
                var entry = new PublishBatchRequestEntry
                {
                    Id = envelope.Id.ToString(),
                    Message = topic.Mapper!.BuildMessageBody(envelope)
                };
                
                if (envelope.GroupId.IsNotEmpty())
                {
                    entry.MessageGroupId = envelope.GroupId;
                }
                if (topic.IsFifoTopic)
                {
                    var deduplicationId = AmazonSnsTopic.DetermineDeduplicationId(envelope);
                    if (deduplicationId.IsNotEmpty())
                    {
                        entry.MessageDeduplicationId = deduplicationId;
                    }
                }

                foreach (var attribute in topic.Mapper.ToAttributes(envelope))
                {
                    entry.MessageAttributes ??= new();
                    entry.MessageAttributes.Add(attribute.Key, attribute.Value);
                }

                entries.Add(entry);
                _envelopes.Add(entry.Id, envelope);
            }
            catch (Exception e)
            {
                logger.LogError(e,
                    "Error while mapping envelope {Envelope} to an SNS PublishBatchRequestEntry. It can never be sent, so it is reported as a serialization failure",
                    envelope);
                _mappingFailures.Add(envelope);
            }
        }

        Request = new PublishBatchRequest
        {
            TopicArn = topic.TopicArn,
            PublishBatchRequestEntries = entries
        };
    }
    
    public async Task ProcessSuccessAsync(ISenderCallback callback, PublishBatchResponse response,
        OutgoingMessageBatch batch)
    {
        if (response.Failed == null || !response.Failed.Any())
        {
            await callback.MarkSuccessfulAsync(batch);
        }
        else
        {
            var fails = new List<Envelope>();
            foreach (var fail in response.Failed ?? [])
            {
                if (_envelopes.TryGetValue(fail.Id, out var env))
                {
                    fails.Add(env);
                }
            }

            var successes = new List<Envelope>();
            foreach (var success in response.Successful ?? [])
            {
                if (_envelopes.TryGetValue(success.Id, out var env))
                {
                    successes.Add(env);
                }
            }

            if (successes.Count > 0)
            {
                await callback.MarkSuccessfulAsync(new OutgoingMessageBatch(batch.Destination, successes));
            }

            await callback.MarkProcessingFailureAsync(new OutgoingMessageBatch(batch.Destination, fails));
        }
    }
}
