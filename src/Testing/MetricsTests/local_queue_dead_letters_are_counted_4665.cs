using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace MetricsTests;

/// <summary>
/// GH-4665. A dead letter from a *user's* local queue never reached the per-type / per-tenant
/// <c>DeadLetters</c> metric, while executions and failures for the same message type and tenant were
/// counted — so the published row read <c>failures=8 deadLetters=0</c> while the dead-letter table held
/// the envelopes.
///
/// <para>
/// The gate in <c>MovedToErrorQueue</c> skipped every <c>local://</c> destination, and the two publishing
/// trackers' own <c>MovedToErrorQueue</c> overrides are unreachable for this path — since GH-3774
/// <c>CompletionTrackerFor</c> diverts only to the metrics-silent tracker and everything else falls
/// through to the runtime-global method — so nothing else counted them either.
/// </para>
///
/// <para>
/// There was no test anywhere asserting that a dead letter reaches the accumulator at all, which is what
/// let this through. This is that test.
/// </para>
/// </summary>
public class local_queue_dead_letters_are_counted_4665 : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime _runtime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Metrics.Mode = WolverineMetricsMode.CritterWatch;
                opts.PublishMessage<DeadLetteredMessage>().ToLocalQueue("orders");
            }).StartAsync();

        _runtime = _host.GetRuntime();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Theory]
    // The bug: an ordinary local queue. Was 0 before the fix.
    [InlineData("local://orders", 1)]
    // Still suppressed, because LocalTransport marks these EndpointRole.System.
    [InlineData("local://durable", 0)]
    [InlineData("local://agents", 0)]
    // Always counted -- the control that shows the pipeline works and the assertion is not vacuous.
    [InlineData("rabbitmq://queue/incoming", 1)]
    public async Task dead_letters_reach_the_per_type_accumulator(string destination, int expected)
    {
        var uri = new Uri(destination);

        var envelope = new Envelope(new DeadLetteredMessage("one"))
        {
            MessageType = typeof(DeadLetteredMessage).FullName!,
            Destination = uri,
            TenantId = "globex"
        };

        _runtime.MovedToErrorQueue(envelope, new InvalidOperationException("nope"));

        var accumulator = _runtime.MetricsAccumulator.FindAccumulator(envelope.MessageType, uri);

        // The post goes through a batching Block, so let it drain before reading the export.
        await accumulator.EntryPoint.WaitForCompletionAsync();

        var metrics = accumulator.TriggerExport(1);

        var deadLetters = metrics.PerTenant
            .SelectMany(x => x.Exceptions)
            .Sum(x => x.DeadLetters);

        deadLetters.ShouldBe(expected);
    }
}

public record DeadLetteredMessage(string Name);

public static class DeadLetteredMessageHandler
{
    public static void Handle(DeadLetteredMessage message)
    {
    }
}
