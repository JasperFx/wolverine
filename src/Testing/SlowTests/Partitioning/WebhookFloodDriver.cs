using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests.Partitioning;

namespace SlowTests.Partitioning;

/// <summary>
/// GH-3713. The webhook-flood publisher: a sustained, skewed stream of grouped events pushed at the cluster
/// from whichever nodes are alive at that instant.
/// </summary>
/// <remarks>
/// <para><b>Why it picks a live host per send.</b> The chaos phases deliberately take nodes away mid-flood,
/// and a publisher pinned to one host would simply die with it. Picking from the currently-live set each time
/// keeps the flood sustained <i>across</i> the disruption, which is the only way the disruption is genuinely
/// mid-flood rather than between floods.</para>
///
/// <para><b>Why a send is only counted after it returns.</b> Completeness is asserted against this list, so a
/// send that threw because its host was being disposed underneath it must not be counted -- Wolverine never
/// accepted it, so nothing is entitled to deliver it. Counting it would manufacture phantom message loss.</para>
///
/// <para><b>Pacing, and why the chaos phases do not use it.</b> <see cref="TimeSpan.Zero" /> means publish as
/// fast as the senders can go; anything else spreads the arrivals evenly across that span. The first cut of
/// this suite paced every phase, and it measured a duplicate rate of exactly zero in all of them -- for the
/// uninteresting reason that a paced arrival rate well under the cluster's capacity leaves the queues empty,
/// so a node killed "mid-flood" was killed with almost nothing unacknowledged. A flood is a burst, and only a
/// burst produces the deep unacked window the measurement is about, so the chaos phases publish unpaced.</para>
/// </remarks>
internal sealed class WebhookFloodDriver
{
    /// <summary>A body in the size range a real webhook delivery carries, rather than an empty record.</summary>
    private const string WebhookPayload =
        """
        {"event":"invoice.payment_succeeded","api_version":"2026-01-31","livemode":true,"data":{"object":
        {"amount_paid":249900,"currency":"usd","status":"paid","lines":{"total_count":3},"metadata":
        {"source":"webhook-flood-repro","tier":"enterprise"}}},"pending_webhooks":1,"request":{"key":null}}
        """;

    private readonly string[] _entityStream;
    private readonly Func<IReadOnlyList<IHost>> _liveHosts;
    private readonly ConcurrentQueue<(string GroupId, int Sequence)> _published = new();
    private readonly ConcurrentQueue<Exception> _rejected = new();
    private readonly ConcurrentDictionary<string, int> _sequences = new();
    private readonly TimeSpan _targetDuration;

    public WebhookFloodDriver(string[] entityStream, Func<IReadOnlyList<IHost>> liveHosts, TimeSpan targetDuration)
    {
        _entityStream = entityStream;
        _liveHosts = liveHosts;
        _targetDuration = targetDuration;
    }

    /// <summary>Every event the cluster actually accepted, as (entity id, sequence) identity pairs.</summary>
    public IReadOnlyList<(string GroupId, int Sequence)> Published => _published.ToArray();

    /// <summary>
    /// Sends refused because their host was going away. Not message loss -- see the class remarks.
    /// </summary>
    public int RejectedSends => _rejected.Count;

    /// <summary>
    /// A skewed entity stream: a hot tenth of the entities takes half the traffic, the rest spread over the
    /// long tail. Flat traffic would put roughly one message per entity in flight at a time and make
    /// intra-group overlap nearly impossible to hit by accident, which would make a passing invariant
    /// assertion meaningless.
    /// </summary>
    public static string[] BuildSkewedEntityStream(int entityCount, int totalMessages, int seed)
    {
        if (entityCount < 10)
        {
            throw new ArgumentOutOfRangeException(nameof(entityCount), "Need at least ten entities to skew");
        }

        var random = new Random(seed);
        var run = Guid.NewGuid().ToString("N")[..8];
        var entities = Enumerable.Range(0, entityCount).Select(i => $"entity-{run}-{i:D4}").ToArray();

        var hotCount = Math.Max(1, entityCount / 10);

        return Enumerable.Range(0, totalMessages)
            .Select(_ => random.NextDouble() < 0.5
                ? entities[random.Next(hotCount)]
                : entities[hotCount + random.Next(entityCount - hotCount)])
            .ToArray();
    }

    /// <summary>
    /// Run the flood to completion. Publishes on several concurrent senders so the send path is not itself
    /// the bottleneck that the measurement ends up measuring.
    /// </summary>
    public async Task RunAsync(int senderCount, CancellationToken token)
    {
        var unpaced = _targetDuration <= TimeSpan.Zero;
        var perMessageDelay = unpaced ? 0 : _targetDuration.TotalMilliseconds / Math.Max(1, _entityStream.Length);
        var started = Stopwatch.StartNew();

        var chunks = Enumerable.Range(0, senderCount)
            .Select(offset => _entityStream.Where((_, i) => i % senderCount == offset).ToArray())
            .ToArray();

        await Task.WhenAll(chunks.Select((chunk, index) => Task.Run(async () =>
        {
            for (var i = 0; i < chunk.Length; i++)
            {
                if (token.IsCancellationRequested) return;

                // Pace against the elapsed wall clock rather than sleeping a fixed slice per message, so a
                // sender that fell behind during a disruption catches back up instead of stretching the
                // flood out behind the very chaos it was supposed to overlap.
                if (!unpaced)
                {
                    var due = perMessageDelay * (i * senderCount + index);
                    var behind = due - started.Elapsed.TotalMilliseconds;
                    if (behind > 1)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(behind), CancellationToken.None);
                    }
                }

                await publishAsync(chunk[i]);
            }
        }, CancellationToken.None)));
    }

    private async Task publishAsync(string entityId)
    {
        var hosts = _liveHosts();
        if (hosts.Count == 0)
        {
            return;
        }

        var sequence = _sequences.AddOrUpdate(entityId, 0, (_, current) => current + 1);
        var host = hosts[Random.Shared.Next(hosts.Count)];

        try
        {
            await host.MessageBus().PublishAsync(new NativeAckLetter(entityId, sequence, WebhookPayload));
            _published.Enqueue((entityId, sequence));
        }
        catch (Exception e)
        {
            // The host was being disposed underneath this send. Deliberately NOT counted as published.
            _rejected.Enqueue(e);
        }
    }
}
