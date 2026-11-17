using Dekaf.Producer;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// Produces a batch of records and reports how each one fared. The broker acks every record on its own, and
/// <c>ProduceAllAsync</c> only surfaces the first failure, so it cannot say which records were acked.
/// </summary>
internal sealed class KafkaBatchDelivery
{
    private readonly Exception?[] _failures;
    private readonly int[] _reported;
    private int _remaining;

    private readonly TaskCompletionSource<Exception?[]> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private KafkaBatchDelivery(int count)
    {
        _failures = new Exception?[count];
        _reported = new int[count];
        _remaining = count;
    }

    /// <summary>
    /// Hands every record to the producer, which still batches them, and completes once each has been acked or
    /// has failed. The result holds each record's failure, or null for a record the broker acked.
    /// </summary>
    public static async Task<Exception?[]> ProduceAsync(IKafkaProducer<string, byte[]> producer,
        IReadOnlyList<ProducerMessage<string, byte[]>> messages)
    {
        if (messages.Count == 0) return [];

        var delivery = new KafkaBatchDelivery(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            var index = i;
            try
            {
                await producer.FireAsync(messages[i], (_, failure) => delivery.report(index, failure));
            }
            catch (Exception e)
            {
                // Rejected before it was queued, so its delivery handler will not run
                delivery.report(index, e);
            }
        }

        return await delivery._completion.Task;
    }

    private void report(int index, Exception? failure)
    {
        // A record reports once, whether through its delivery handler or a synchronous rejection
        if (Interlocked.Exchange(ref _reported[index], 1) != 0) return;

        _failures[index] = failure;
        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            _completion.TrySetResult(_failures);
        }
    }
}
