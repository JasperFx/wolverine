using System.Diagnostics;
using System.Runtime.CompilerServices;
using Wolverine.Runtime;

namespace Wolverine.Http.Grpc;

/// <summary>
/// Extension methods to add OpenTelemetry tracing to gRPC streaming operations
/// </summary>
public static class StreamingTelemetryExtensions
{
    /// <summary>
    /// Wraps an async enumerable with OpenTelemetry tracing for streaming operations.
    /// Tracks stream start, each yielded message, completion, and errors.
    /// </summary>
    /// <typeparam name="T">The type of messages being streamed</typeparam>
    /// <param name="source">The source stream to wrap</param>
    /// <param name="correlationId">Optional correlation ID for distributed tracing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A traced async enumerable</returns>
    public static async IAsyncEnumerable<T> WithTelemetry<T>(
        this IAsyncEnumerable<T> source,
        string? correlationId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var activity = WolverineTracing.StartStreaming<T>(correlationId);
        var stopwatch = Stopwatch.StartNew();
        var messageCount = 0;
        Exception? caughtException = null;

        var enumerator = source.GetAsyncEnumerator(cancellationToken);
        await using var _ = enumerator.ConfigureAwait(false);

        while (true)
        {
            T item;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }
                item = enumerator.Current;
            }
            catch (Exception ex)
            {
                caughtException = ex;
                break;
            }

            messageCount++;
            activity.RecordStreamedMessage(item);
            yield return item;
        }

        stopwatch.Stop();
        if (caughtException != null)
        {
            activity.RecordStreamingError(caughtException);
            activity.CompleteStreaming(messageCount, stopwatch.Elapsed);
            throw caughtException;
        }

        activity.CompleteStreaming(messageCount, stopwatch.Elapsed);
    }

    /// <summary>
    /// Creates a traced async enumerable from a handler function.
    /// Useful for wrapping streaming handlers with telemetry.
    /// </summary>
    /// <typeparam name="T">The type of messages being streamed</typeparam>
    /// <param name="streamFactory">Function that produces the stream</param>
    /// <param name="correlationId">Optional correlation ID for distributed tracing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A traced async enumerable</returns>
    public static IAsyncEnumerable<T> StreamWithTelemetry<T>(
        Func<IAsyncEnumerable<T>> streamFactory,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        return streamFactory().WithTelemetry(correlationId, cancellationToken);
    }
}
