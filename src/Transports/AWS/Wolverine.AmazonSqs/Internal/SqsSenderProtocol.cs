using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using JasperFx.Core;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSqs.Internal;

internal class SqsSenderProtocol :ISenderProtocol
{
    private readonly ILogger _logger;
    private readonly AmazonSqsQueue _queue;
    private readonly IAmazonSQS _sqs;

    public SqsSenderProtocol(IWolverineRuntime runtime, AmazonSqsQueue queue, IAmazonSQS sqs)
    {
        _queue = queue;
        _sqs = sqs;
        _logger = runtime.LoggerFactory.CreateLogger<SqsSenderProtocol>();

        _queue.Mapper ??= _queue.BuildMapper(runtime);
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        await _queue.InitializeAsync(_logger);

        // SQS has a hard limit of 10 messages per batch request
        var chunks = batch.Messages.Chunk(10);

        try
        {
            foreach (var chunk in chunks)
            {
                var sqsBatch = new OutgoingSqsBatch(_queue, _logger, chunk);
                await _sqs.SendMessageBatchAsync(sqsBatch.Request);
            }

            await callback.MarkSuccessfulAsync(batch);
        }
        catch (Exception e)
        {
            await callback.MarkProcessingFailureAsync(batch, e);
        }
    }
}

internal class OutgoingSqsBatch
{
    private readonly Dictionary<string, Envelope> _envelopes = new();

    public OutgoingSqsBatch(AmazonSqsQueue queue, ILogger logger, IEnumerable<Envelope> envelopes)
    {
        var entries = new List<SendMessageBatchRequestEntry>();
        foreach (var envelope in envelopes)
        {
            try
            {
                var entry = new SendMessageBatchRequestEntry(envelope.Id.ToString(), queue.Mapper.BuildMessageBody(envelope));
                if (queue.IsFifoQueue)
                {
                    if (envelope.GroupId.IsNotEmpty())
                    {
                        entry.MessageGroupId = envelope.GroupId;
                    }
                    if (envelope.DeduplicationId.IsNotEmpty())
                    {
                        entry.MessageDeduplicationId = envelope.DeduplicationId;
                    }
                }

                foreach (var attribute in queue.Mapper.ToAttributes(envelope))
                {
                    entry.MessageAttributes ??= new Dictionary<string, MessageAttributeValue>();
                    entry.MessageAttributes.Add(attribute.Key, attribute.Value);
                }

                entries.Add(entry);
                _envelopes.Add(entry.Id, envelope);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error while mapping envelope {Envelope} to an SQS SendMessageBatchRequestEntry",
                    envelope);
            }
        }

        Request = new SendMessageBatchRequest(queue.QueueUrl, entries);
    }

    public SendMessageBatchRequest Request { get; }

    public bool TryGetEnvelope(string id, out Envelope envelope)
    {
        return _envelopes.TryGetValue(id, out envelope);
    }
}
