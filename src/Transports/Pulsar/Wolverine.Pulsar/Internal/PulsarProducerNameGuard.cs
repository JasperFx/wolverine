using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DotPulsar;
using DotPulsar.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Wolverine.Pulsar.Internal;

/// <summary>
///     GH-5847. Pulsar allows one connected producer per name on a topic. With <c>EnableDeduplication()</c> Wolverine
///     names its producers, and a second running instance of the same service asked for the same name. The broker
///     refused it (ProducerBusy), DotPulsar retried every few seconds forever, and that instance's sends hung.
///
///     <para>This is a DotPulsar exception handler on the transport's client. It only looks at
///     <see cref="ProducerBusyException" /> for a producer name and topic Wolverine registered, and lets DotPulsar keep
///     retrying until the refusals have gone on for <see cref="Timeout" />. A short run of them is normal: the broker
///     still holds the producer of a process that just died until it notices the connection is gone. Past the timeout
///     the producer is failed, an error naming the fix is logged once, and the sender throws
///     <see cref="PulsarProducerNameInUseException" />. Every other failure is left to DotPulsar.</para>
/// </summary>
internal sealed class PulsarProducerNameGuard
{
    // The broker's message, as DotPulsar passes it on:
    // "...NamingException: Producer with name 'X' is already connected to topic 'Y'"
    private static readonly Regex BusyMessage = new("Producer with name '(?<name>.+?)' is already connected to topic '(?<topic>.+?)'",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ConcurrentDictionary<(string Name, string Topic), Conflict> _producers = new();

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>
    ///     A refusal more than this long after the previous one starts a new run of them: the producer connected in
    ///     between. DotPulsar retries a refused producer every 3 seconds.
    /// </summary>
    internal static readonly TimeSpan RunGap = TimeSpan.FromSeconds(15);

    public void Register(string producerName, string topic)
    {
        _producers.TryAdd((producerName, topic), new Conflict());
    }

    public PulsarProducerNameInUseException? ErrorFor(string producerName, string topic)
    {
        return _producers.TryGetValue((producerName, topic), out var conflict) ? conflict.Error : null;
    }

    public ValueTask OnException(ExceptionContext context)
    {
        if (TryFail(context.Exception, DateTimeOffset.UtcNow, out _))
        {
            context.Result = FaultAction.ThrowException;
            context.ExceptionHandled = true;
        }

        return ValueTask.CompletedTask;
    }

    public bool TryFail(Exception exception, DateTimeOffset now, out PulsarProducerNameInUseException? error)
    {
        error = null;

        if (exception is not ProducerBusyException)
        {
            return false;
        }

        var match = BusyMessage.Match(exception.Message);
        if (!match.Success)
        {
            return false;
        }

        var name = match.Groups["name"].Value;
        var topic = match.Groups["topic"].Value;

        if (!_producers.TryGetValue((name, topic), out var conflict))
        {
            // A partitioned topic has one producer per partition, and the broker names the partition
            var partition = topic.LastIndexOf("-partition-", StringComparison.Ordinal);
            if (partition <= 0)
            {
                return false;
            }

            topic = topic[..partition];
            if (!_producers.TryGetValue((name, topic), out conflict))
            {
                return false;
            }
        }

        lock (conflict)
        {
            if (conflict.Error != null)
            {
                error = conflict.Error;
                return true;
            }

            if (conflict.FirstRefused is null || now - conflict.LastRefused > RunGap)
            {
                conflict.FirstRefused = now;
            }

            conflict.LastRefused = now;

            var refusedFor = now - conflict.FirstRefused.Value;
            if (refusedFor < Timeout)
            {
                return false;
            }

            conflict.Error = new PulsarProducerNameInUseException(name, topic, refusedFor, exception);
            error = conflict.Error;
        }

        Logger.LogError(error, "{Message}", error.Message);
        return true;
    }

    private sealed class Conflict
    {
        public DateTimeOffset? FirstRefused { get; set; }
        public DateTimeOffset LastRefused { get; set; }
        public PulsarProducerNameInUseException? Error { get; set; }
    }
}
