using System.Text.Json;
using JasperFx.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.SignalR.Client;
using Wolverine.SignalR.Internals;
using Wolverine.Tracking;
using Wolverine.Util;

namespace Wolverine.SignalR.Tests;

/// <summary>
///     GH-3972. Coalescing outbound messages into one envelope per destination on a flush interval.
/// </summary>
public class outgoing_coalescing : IAsyncLifetime
{
    private WebApplication theWebApp = null!;
    private readonly int Port = PortFinder.GetAvailablePort();
    private readonly List<IHost> _clientHosts = new();

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(opts => opts.ListenLocalhost(Port));

        builder.Services.AddSignalR();
        builder.Host.UseWolverine(opts =>
        {
            opts.ServiceName = "Server";
            opts.UseSignalR();

            #region sample_signalr_coalesce_outgoing
            opts.PublishMessage<FromFirst>().ToSignalR()
                .CoalesceOutgoing(o =>
                {
                    o.FlushInterval = 100.Milliseconds();
                    o.MaxBatchSize = 200;
                });
            #endregion
        });

        var app = builder.Build();
        app.MapWolverineSignalRHub();
        await app.StartAsync();

        theWebApp = app;
    }

    private async Task<IHost> startClientHost()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Client";
                opts.UseClientToSignalR(Port);
            }).StartAsync();

        _clientHosts.Add(host);
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        await theWebApp.StopAsync();
        await theWebApp.DisposeAsync();

        foreach (var clientHost in _clientHosts)
        {
            await clientHost.StopAsync();
            clientHost.Dispose();
        }
    }

    /// <summary>
    ///     The round trip that matters: several messages published in one window arrive at the client as
    ///     individual messages again, in the order they were sent. If the wrapper or the client unwrap were
    ///     wrong, they would either not arrive or arrive as unreadable payloads.
    /// </summary>
    [Fact]
    public async Task coalesced_messages_round_trip_to_the_client_in_order()
    {
        using var client = await startClientHost();

        var names = Enumerable.Range(0, 10).Select(i => $"Receiver-{i}").ToArray();

        var tracked = await theWebApp
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(client)
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(new Func<IMessageContext, Task>(async c =>
            {
                foreach (var name in names)
                {
                    await c.PublishAsync(new FromFirst(name));
                }
            }));

        var received = tracked.Received.RecordsInOrder()
            .Where(x => x.Envelope?.Message is FromFirst)
            .Select(x => ((FromFirst)x.Envelope!.Message!).Name)
            .ToArray();

        received.Length.ShouldBe(names.Length);

        // Arrival order within a coalesced envelope, which is what the issue specifies
        received.ShouldBe(names);
    }
}

/// <summary>
///     GH-3972. The parts that do not need a live hub.
/// </summary>
public class outgoing_coalescing_internals
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Web);

    [Fact]
    public void the_wrapper_round_trips_its_items()
    {
        var items = new[] { "{\"a\":1}", "{\"b\":2}", "{\"c\":3}" };

        var json = CoalescedSignalRMessage.ToJson(items, JsonOptions);

        CoalescedSignalRMessage.TryReadItems(json, JsonOptions, out var read).ShouldBeTrue();
        read.ShouldBe(items);
    }

    [Fact]
    public void reading_a_non_batch_payload_fails_rather_than_guessing()
    {
        CoalescedSignalRMessage.TryReadItems("this is not json", JsonOptions, out _).ShouldBeFalse();
    }

    [Fact]
    public void flush_interval_must_be_positive()
    {
        var options = new OutgoingCoalescingOptions();
        Should.Throw<ArgumentOutOfRangeException>(() => options.FlushInterval = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.FlushInterval = -1.Seconds());
    }

    [Fact]
    public void max_batch_size_must_be_at_least_one()
    {
        var options = new OutgoingCoalescingOptions();
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxBatchSize = 0);
    }

    [Fact]
    public void has_sane_defaults()
    {
        var options = new OutgoingCoalescingOptions();
        options.FlushInterval.ShouldBe(100.Milliseconds());
        options.MaxBatchSize.ShouldBe(200);
    }

    /// <summary>
    ///     The correctness property the issue calls out: "Key the buffer by destination (connection / group).
    ///     An app that only ever broadcasts gets away with one global queue; the transport must not, or it
    ///     cross-delivers."
    /// </summary>
    [Fact]
    public async Task buffers_are_keyed_by_destination_so_messages_never_cross_deliver()
    {
        var hub = new RecordingHubContext();
        var options = new OutgoingCoalescingOptions { MaxBatchSize = 2 };

        await using var coalescer = new OutgoingCoalescer(options, hub.Context, JsonOptions, null);

        // Two messages for connection A and two for connection B, interleaved. With a single global
        // buffer, MaxBatchSize = 2 would flush A's first message together with B's first.
        await coalescer.EnqueueAsync(new WebSocketRouting.Connection("A"), "op", "\"a1\"");
        await coalescer.EnqueueAsync(new WebSocketRouting.Connection("B"), "op", "\"b1\"");
        await coalescer.EnqueueAsync(new WebSocketRouting.Connection("A"), "op", "\"a2\"");
        await coalescer.EnqueueAsync(new WebSocketRouting.Connection("B"), "op", "\"b2\"");

        var toA = hub.SendsTo("A");
        var toB = hub.SendsTo("B");

        toA.Count.ShouldBe(1);
        toB.Count.ShouldBe(1);

        CoalescedSignalRMessage.TryReadItems(toA[0].Payload, JsonOptions, out var aItems).ShouldBeTrue();
        CoalescedSignalRMessage.TryReadItems(toB[0].Payload, JsonOptions, out var bItems).ShouldBeTrue();

        aItems.ShouldBe(["\"a1\"", "\"a2\""]);
        bItems.ShouldBe(["\"b1\"", "\"b2\""]);
    }

    [Fact]
    public async Task a_batch_of_one_is_sent_on_the_normal_operation()
    {
        var hub = new RecordingHubContext();
        var options = new OutgoingCoalescingOptions { MaxBatchSize = 1 };

        await using var coalescer = new OutgoingCoalescer(options, hub.Context, JsonOptions, null);

        await coalescer.EnqueueAsync(new WebSocketRouting.Connection("A"), SignalRTransport.DefaultOperation,
            "\"only\"");

        var sends = hub.SendsTo("A");
        sends.Count.ShouldBe(1);

        // Not wrapped -- a client that never opted into coalescing still works for the trickle case
        sends[0].Operation.ShouldBe(SignalRTransport.DefaultOperation);
        sends[0].Payload.ShouldBe("\"only\"");
    }

    /// <summary>
    ///     Drain on shutdown, so messages enqueued just before stop are not dropped.
    /// </summary>
    [Fact]
    public async Task disposing_drains_whatever_is_still_buffered()
    {
        var hub = new RecordingHubContext();

        // Long interval and a high ceiling, so nothing flushes on its own
        var options = new OutgoingCoalescingOptions { FlushInterval = 5.Minutes(), MaxBatchSize = 1000 };

        var coalescer = new OutgoingCoalescer(options, hub.Context, JsonOptions, null);

        await coalescer.EnqueueAsync(new WebSocketRouting.Connection("A"), "op", "\"a1\"");
        await coalescer.EnqueueAsync(new WebSocketRouting.Connection("A"), "op", "\"a2\"");

        hub.SendsTo("A").ShouldBeEmpty("nothing should have flushed yet");

        await coalescer.DisposeAsync();

        var sends = hub.SendsTo("A");
        sends.Count.ShouldBe(1);
        CoalescedSignalRMessage.TryReadItems(sends[0].Payload, JsonOptions, out var items).ShouldBeTrue();
        items.ShouldBe(["\"a1\"", "\"a2\""]);
    }
}

/// <summary>
///     Captures what the coalescer actually sent, and to whom. Hand written rather than substituted:
///     <c>SendAsync</c> is an extension method over <see cref="IClientProxy.SendCoreAsync" />, and the
///     indirection makes a mocked setup easy to get subtly wrong in a way that silently records nothing.
/// </summary>
public class RecordingHubContext
{
    private readonly Dictionary<string, List<SentMessage>> _byDestination = new();
    private readonly object _lock = new();

    public record SentMessage(string Operation, string Payload);

    public IHubContext<Hub> Context { get; }

    public RecordingHubContext()
    {
        Context = new FakeHubContext(this);
    }

    internal void Record(string destination, SentMessage message)
    {
        lock (_lock)
        {
            if (!_byDestination.TryGetValue(destination, out var list))
            {
                list = [];
                _byDestination[destination] = list;
            }

            list.Add(message);
        }
    }

    public IReadOnlyList<SentMessage> SendsTo(string destination)
    {
        lock (_lock)
        {
            return _byDestination.TryGetValue(destination, out var list) ? list.ToArray() : [];
        }
    }

    private class FakeHubContext(RecordingHubContext parent) : IHubContext<Hub>
    {
        public IHubClients Clients { get; } = new FakeHubClients(parent);
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private class FakeHubClients(RecordingHubContext parent) : IHubClients
    {
        public IClientProxy All => new FakeClientProxy(parent, "All");
        public IClientProxy Client(string connectionId) => new FakeClientProxy(parent, connectionId);
        public IClientProxy Group(string groupName) => new FakeClientProxy(parent, $"Group:{groupName}");

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) =>
            throw new NotSupportedException();

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
            throw new NotSupportedException();

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private class FakeClientProxy(RecordingHubContext parent, string destination) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            parent.Record(destination, new SentMessage(method, (string)args[0]!));
            return Task.CompletedTask;
        }
    }
}
