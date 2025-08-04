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

        var snsBatch = new OutgoingSnsBatch(_topic, _logger, batch.Messages);

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
                    Message = topic.Mapper.BuildMessageBody(envelope)
                };
                
                if (envelope.GroupId.IsNotEmpty())
                {
                    entry.MessageGroupId = envelope.GroupId;
                }
                if (envelope.DeduplicationId.IsNotEmpty())
                {
                    entry.MessageDeduplicationId = envelope.DeduplicationId;
                }

                entries.Add(entry);
                _envelopes.Add(entry.Id, envelope);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error while mapping envelope {Envelope} to an SQS SendMessageBatchRequestEntry",
                    envelope);
                _mappingFailures.Add(envelope);
            }
            
            Request = new PublishBatchRequest
            {
                TopicArn = topic.TopicArn,
                PublishBatchRequestEntries = entries
            };
        }
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

            await callback.MarkSuccessfulAsync(new OutgoingMessageBatch(batch.Destination,
                successes.Concat(_mappingFailures).ToList()));

            await callback.MarkProcessingFailureAsync(new OutgoingMessageBatch(batch.Destination, fails));
        }
    }
}
