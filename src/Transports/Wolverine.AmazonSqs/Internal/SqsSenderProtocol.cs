using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSqs.Internal;

internal class InlineSqsSender : ISender
{
    private readonly ILogger _logger;
    private readonly AmazonSqsMapper _mapper;
    private readonly AmazonSqsQueue _queue;
    private readonly IAmazonSQS _sqs;
    
    public InlineSqsSender(IWolverineRuntime runtime, AmazonSqsQueue queue, IAmazonSQS sqs)
    {
        _queue = queue;
        _sqs = sqs;
        _mapper = queue.BuildMapper(runtime);
        _logger = runtime.LoggerFactory.CreateLogger<InlineSqsSender>();
    }

    public bool SupportsNativeScheduledSend { get; } = false;
    public Uri Destination => _queue.Uri;
    public async Task<bool> PingAsync()
    {
        var envelope = Envelope.ForPing(Destination);
        try
        {
            await SendAsync(envelope);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        await _queue.InitializeAsync(_logger);

        // TODO -- This is awful. See if this could be collapsed. The mapping I mean
        var entry = new SendMessageBatchRequestEntry(envelope.Id.ToString(),
            Encoding.Default.GetString(envelope.Data!));
        _mapper.MapEnvelopeToOutgoing(envelope, entry);

        var request = new SendMessageRequest(_queue.QueueUrl, entry.MessageBody);
        foreach (var pair in entry.MessageAttributes)
        {
            request.MessageAttributes[pair.Key] = pair.Value;
        }

        foreach (var pair in entry.MessageSystemAttributes)
        {
            request.MessageSystemAttributes[pair.Key] = pair.Value;
        }

        await _sqs.SendMessageAsync(request);
    }
}

internal class SqsSenderProtocol : ISenderProtocol
{
    private readonly ILogger _logger;
    private readonly AmazonSqsMapper _mapper;
    private readonly AmazonSqsQueue _queue;
    private readonly IAmazonSQS _sqs;

    public SqsSenderProtocol(IWolverineRuntime runtime, AmazonSqsQueue queue, IAmazonSQS sqs)
    {
        _queue = queue;
        _sqs = sqs;
        _mapper = queue.BuildMapper(runtime);
        _logger = runtime.LoggerFactory.CreateLogger<SqsSenderProtocol>();
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        await _queue.InitializeAsync(_logger);

        var sqsBatch = new OutgoingSqsBatch(_queue, _logger, batch.Messages, _mapper);

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

    public OutgoingSqsBatch(AmazonSqsQueue queue, ILogger logger, IEnumerable<Envelope> envelopes,
        AmazonSqsMapper mapper)
    {
        var entries = new List<SendMessageBatchRequestEntry>();
        foreach (var envelope in envelopes)
        {
            try
            {
                var entry = new SendMessageBatchRequestEntry(envelope.Id.ToString(),
                    Encoding.Default.GetString(envelope.Data!));
                mapper.MapEnvelopeToOutgoing(envelope, entry);
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