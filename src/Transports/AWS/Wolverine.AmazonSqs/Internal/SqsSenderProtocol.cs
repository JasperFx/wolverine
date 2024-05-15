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
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        await _queue.InitializeAsync(_logger);

        var sqsBatch = new OutgoingSqsBatch(_queue, _logger, batch.Messages);

        try
        {
            var response = await _sqs.SendMessageBatchAsync(sqsBatch.Request);

            await sqsBatch.ProcessSuccessAsync(callback, response, batch);
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
    private readonly List<Envelope> _mappingFailures = new();

    public OutgoingSqsBatch(AmazonSqsQueue queue, ILogger logger, IEnumerable<Envelope> envelopes)
    {
        var entries = new List<SendMessageBatchRequestEntry>();
        foreach (var envelope in envelopes)
        {
            try
            {
                var entry = new SendMessageBatchRequestEntry(envelope.Id.ToString(), queue.Mapper.BuildMessageBody(envelope));
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
        }

        Request = new SendMessageBatchRequest(queue.QueueUrl, entries);
    }

    public SendMessageBatchRequest Request { get; }

    public async Task ProcessSuccessAsync(ISenderCallback callback, SendMessageBatchResponse response,
        OutgoingMessageBatch batch)
    {
        if (!response.Failed.Any())
        {
            await callback.MarkSuccessfulAsync(batch);
        }
        else
        {
            var fails = new List<Envelope>();
            foreach (var fail in response.Failed)
            {
                if (_envelopes.TryGetValue(fail.Id, out var env))
                {
                    fails.Add(env);
                }
            }

            var successes = new List<Envelope>();
            foreach (var success in response.Successful)
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