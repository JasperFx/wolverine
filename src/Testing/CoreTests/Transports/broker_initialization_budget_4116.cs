using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using CoreTests.Runtime;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Transports;

/// <summary>
/// GH-4116. BrokerTransport.InitializeAsync retried a failed startup twenty times with a five second pause,
/// and nothing bounded it by a clock. "Twenty attempts" bounds tries, not time -- and one try costs whatever
/// the broker client's own request timeout is, which is 60s for librdkafka. Against an unreachable Kafka
/// broker that made a Wolverine host take over twenty minutes to fail to start, measured. Longer than any
/// sane orchestrator start probe, and longer than this repository's own 20-minute CI job cap, which is what
/// turns a readable startup failure into a job cancellation whose logs are then discarded (GH-4098).
///
/// The delay also ignored cancellation outright, so a host being shut down could not break out of the loop.
/// </summary>
public class broker_initialization_budget_4116
{
    [Fact]
    public async Task gives_up_on_the_budget_rather_than_on_the_attempt_count()
    {
        var runtime = new MockWolverineRuntime();
        runtime.Options.BrokerInitializationTimeout = 2.Seconds();

        var transport = new SlowFailingTransport { AttemptDuration = 250.Milliseconds() };

        var stopwatch = Stopwatch.StartNew();
        await Should.ThrowAsync<BrokerInitializationException>(async () =>
            await transport.InitializeAsync(runtime));
        stopwatch.Stop();

        // Twenty attempts at 250ms plus nineteen five-second pauses is ~100 seconds. The budget has to be
        // what ends this, not the attempt count.
        transport.Attempts.ShouldBeLessThan(20);
        stopwatch.Elapsed.ShouldBeLessThan(30.Seconds());
    }

    [Fact]
    public async Task a_cancelled_runtime_breaks_out_of_the_retry_pause()
    {
        using var cancellation = new CancellationTokenSource();

        var runtime = new MockWolverineRuntime
        {
            Cancellation = cancellation.Token
        };

        // Long enough that only cancellation can end this in any reasonable time.
        runtime.Options.BrokerInitializationTimeout = 10.Minutes();

        var transport = new SlowFailingTransport
        {
            AttemptDuration = TimeSpan.Zero,
            OnAttempt = attempt =>
            {
                if (attempt == 2) cancellation.Cancel();
            }
        };

        var stopwatch = Stopwatch.StartNew();
        await Should.ThrowAsync<BrokerInitializationException>(async () =>
            await transport.InitializeAsync(runtime));
        stopwatch.Stop();

        transport.Attempts.ShouldBe(2);

        // Before GH-4116 the pause was an untokened Task.Delay, so cancelling here bought nothing and the
        // loop ran all twenty attempts across ~95 seconds of pauses.
        stopwatch.Elapsed.ShouldBeLessThan(30.Seconds());
    }

    [Fact]
    public async Task a_successful_start_still_costs_one_attempt_and_no_pause()
    {
        var runtime = new MockWolverineRuntime();
        var transport = new SlowFailingTransport { AttemptDuration = TimeSpan.Zero, SucceedOnAttempt = 1 };

        await transport.InitializeAsync(runtime);

        transport.Attempts.ShouldBe(1);
    }
}

/// <summary>Stands in for a broker whose provisioning call is slow and then fails.</summary>
public class SlowFailingTransport : BrokerTransport<FakeEndpoint>
{
    public SlowFailingTransport() : base("slowfake", "SlowFake", ["fake"])
    {
    }

    public TimeSpan AttemptDuration { get; set; } = TimeSpan.Zero;

    /// <summary>Attempt number (1-based) on which ConnectAsync should succeed instead of throwing.</summary>
    public int? SucceedOnAttempt { get; set; }

    public Action<int>? OnAttempt { get; set; }

    public int Attempts { get; private set; }

    protected override IEnumerable<FakeEndpoint> endpoints() => [];

    protected override FakeEndpoint findEndpointByUri(Uri uri) => throw new NotSupportedException();

    public override Uri ResourceUri { get; } = new("slowfake://transport");

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        Attempts++;
        OnAttempt?.Invoke(Attempts);

        if (AttemptDuration > TimeSpan.Zero)
        {
            await Task.Delay(AttemptDuration);
        }

        if (SucceedOnAttempt.HasValue && Attempts >= SucceedOnAttempt.Value) return;

        throw new TimeoutException("The broker did not answer");
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns() => [];
}
