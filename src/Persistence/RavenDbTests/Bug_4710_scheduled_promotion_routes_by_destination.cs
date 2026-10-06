using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace RavenDbTests;

/// <summary>
/// Reproduction for https://github.com/JasperFx/wolverine/issues/4710.
///
/// <para>
/// Every relational store's scheduled poller ends promotion by handing the batch to
/// <c>runtime.EnqueueDirectlyAsync(envelopes)</c>, which routes each envelope by its <c>Destination</c>.
/// RavenDb's <c>locallyPublishScheduledMessages</c> does not: it enqueues EVERY promoted envelope onto this
/// node's own local durable queue regardless of where it was addressed. The issue asks whether that actually
/// costs anything at runtime, and says so explicitly -- the structural difference is certain, the consequence
/// was read from the code rather than observed.
/// </para>
///
/// <para>
/// <b>It does not, for the shape that reaches this code.</b> A delayed message addressed to an external
/// endpoint does not park in the inbox under that destination. It parks as a <c>ScheduledEnvelope</c>
/// <em>wrapper</em> addressed to this node's own <c>local://durable/</c>, and when the poller promotes it and
/// enqueues it locally, <c>ScheduledSendEnvelopeHandler</c> unwraps it and sends it on to the real
/// destination. Enqueuing locally is therefore exactly right for this shape rather than a defect -- which is
/// the one thing the issue could not settle from source, and the reason these tests exist.
/// </para>
///
/// <para>
/// Two shapes were checked, and neither reaches the poller carrying an external destination:
/// <list type="bullet">
///   <item>A <b>non-durable</b> external destination gives the wrapper in the inbox, which is what this
///   fixture exercises.</item>
///   <item>A <b>durable</b> external destination schedules in the OUTBOX instead and never touches the
///   scheduled inbox poller at all. Worth stating, because reaching for a durable outbox to make the send
///   observable is precisely what makes this test vacuous -- it moves the envelope off the code under test.
///   Hence the second host below instead.</item>
/// </list>
/// </para>
///
/// <para>
/// That receiving host is what keeps the negative assertion honest: "the handler did not run on the promoting
/// node" is equally true of an envelope that was silently dropped, so something has to prove it arrived. TCP
/// is used because it needs no broker alongside the embedded Raven server.
/// </para>
/// </summary>
[Collection("raven")]
public class Bug_4710_scheduled_promotion_routes_by_destination : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;

    // PortFinder rather than a hand-rolled counter: xUnit builds one instance per test method and the suite
    // shares a machine with every other test project.
    private readonly int _port = PortFinder.GetAvailablePort();

    private IHost _promoter = null!;
    private IHost _receiver = null!;
    private IDocumentStore _store = null!;

    public Bug_4710_scheduled_promotion_routes_by_destination(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        Gh4710Tracker.Reset();

        // RavenTestDriver hands back a brand new database per call, and xUnit builds one instance of this
        // class per test method, so each scenario gets an isolated inbox and its own port.
        _store = _fixture.StartRavenStore();

        // The node that receives the promoted send. ONLY this one knows how to handle Gh4710Check, so
        // "handled" and "handled here" cannot be confused.
        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ListenAtPort(_port);
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Gh4710CheckHandler));
            }).StartAsync();

        _promoter = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Both knobs: ScheduledJobFirstExecution defaults to a RANDOM 500-5000ms, which would make
                // this test mostly a stopwatch and intermittently a liar.
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.UseRavenDbPersistence();
                opts.Services.AddSingleton(_store);

                // Deliberately NOT UseDurableOutbox(): a durable destination schedules in the outbox and
                // never reaches the scheduled inbox poller this test is about.
                opts.PublishMessage<Gh4710Check>().ToPort(_port);

                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Gh4710StartHandler));
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Gh4710Tracker.Reset();
        await _promoter.StopAsync();
        _promoter.Dispose();
        await _receiver.StopAsync();
        _receiver.Dispose();
    }

    private Uri theDestination => $"tcp://localhost:{_port}".ToUri();

    /// <summary>
    /// The finding, asserted directly so that a future change moving this shape onto another path makes the
    /// suite say so rather than silently testing nothing.
    /// </summary>
    [Fact]
    public async Task a_delayed_external_send_parks_as_a_wrapper_on_the_local_durable_queue()
    {
        await _promoter.MessageBus().InvokeAsync(new Gh4710Start(Guid.NewGuid().ToString()),
            TestContext.Current.CancellationToken);

        var row = (await scheduledMessagesAsync()).ShouldHaveSingleItem();

        row.MessageType.ShouldBe(TransportConstants.ScheduledEnvelope);
        row.ReceivedAt.ShouldBe(new Uri("local://durable/"));

        // The crux: it is NOT addressed to the external destination, so a poller that enqueues it locally is
        // not mis-routing anything.
        row.ReceivedAt.ShouldNotBe(theDestination);
    }

    /// <summary>
    /// And the consequence: promoted locally, unwrapped, and delivered to the destination node -- not executed
    /// on the promoting node, and not dropped.
    /// </summary>
    [Fact]
    public async Task the_promoted_wrapper_is_unwrapped_and_delivered_to_its_destination()
    {
        var id = Guid.NewGuid().ToString();

        await _promoter.MessageBus().InvokeAsync(new Gh4710Start(id), TestContext.Current.CancellationToken);

        await waitForAsync(() => Gh4710Tracker.Received.Contains(id),
            () => "The scheduled send never reached the listening node. received=[" +
                  string.Join(", ", Gh4710Tracker.Received) + "]");

        // Settled rather than stranded as Incoming under a live owner. A retained Handled row is the normal
        // idempotency residue, filtered out of recovery by LoadPageOfGloballyOwnedIncomingAsync, so this is
        // not the GH-4645 leak the issue wondered about.
        (await incomingMessagesAsync()).ShouldAllBe(x => x.Status == EnvelopeStatus.Handled);
    }

    private async Task<List<IncomingMessage>> scheduledMessagesAsync()
    {
        using var session = _store.OpenAsyncSession();

        // GH-4848 found this while chasing a red CIRavenDb: a RavenDB query is served from an index, and an
        // index is updated asynchronously after the write, so a query issued straight after InvokeAsync
        // returns can legitimately answer with zero rows for a document that is already stored. The store's
        // own admin query (RavenDbMessageStore.Admin.cs) waits; this one did not, and lost that race on
        // every run on one machine and once in CI, where it read as a routing regression.
        return await session.Query<IncomingMessage>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.Status == EnvelopeStatus.Scheduled)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<IncomingMessage>> incomingMessagesAsync()
    {
        using var session = _store.OpenAsyncSession();
        return await session.Query<IncomingMessage>()
            .Customize(x => x.WaitForNonStaleResults())
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task waitForAsync(Func<bool> condition, Func<string> diagnostic)
    {
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100.Milliseconds());
        }

        throw new TimeoutException(diagnostic());
    }
}

public record Gh4710Start(string Id);

public record Gh4710Check(string Id);

public static class Gh4710Tracker
{
    private static readonly List<string> _received = new();

    public static IReadOnlyList<string> Received
    {
        get
        {
            lock (_received) return _received.ToArray();
        }
    }

    public static void Record(string id)
    {
        lock (_received) _received.Add(id);
    }

    public static void Reset()
    {
        lock (_received) _received.Clear();
    }
}

public static class Gh4710StartHandler
{
    // The shape that produces a scheduled row. It has to go through a transaction: a bare bus.ScheduleAsync
    // outside a handler takes a different path entirely.
    [Transactional]
    public static DeliveryMessage<Gh4710Check> Handle(Gh4710Start command)
    {
        return new Gh4710Check(command.Id).DelayedFor(1.Seconds());
    }
}

/// <summary>
/// Registered ONLY on the receiving host, so a recorded message proves the envelope crossed the wire.
/// </summary>
public static class Gh4710CheckHandler
{
    public static void Handle(Gh4710Check message)
    {
        Gh4710Tracker.Record(message.Id);
    }
}
