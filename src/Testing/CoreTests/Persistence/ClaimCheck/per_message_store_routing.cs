using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Persistence.ClaimCheck.Internal;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Persistence.ClaimCheck;

/// <summary>
/// Coverage for GH-3508: UseClaimCheck can route different messages to different stores. The store a
/// payload was off-loaded to is stamped onto the envelope, so the receiver loads it from the same
/// backend even though the sending and receiving endpoint URIs differ.
/// </summary>
public class per_message_store_routing : IAsyncLifetime
{
    private readonly RecordingInMemoryClaimCheckStore _default = new();
    private readonly RecordingInMemoryClaimCheckStore _routed = new();
    private IHost _publisher = null!;
    private IHost _receiver = null!;

    public async Task InitializeAsync()
    {
        CapturedMessages.Reset();

        var port = PortFinder.GetAvailablePort();

        // Both nodes configure the SAME routes so store keys line up on send and receive.
        void Configure(WolverineOptions opts)
        {
            opts.ApplicationAssembly = typeof(per_message_store_routing).Assembly;
            opts.UseClaimCheck(c =>
            {
                c.Store = _default;                              // default backend
                c.StoreForMessage<BlobStringMessage>(_routed);   // this one type goes elsewhere
            });
        }

        _publisher = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            Configure(opts);
            opts.PublishMessage<BlobStringMessage>().ToPort(port);
            opts.PublishMessage<BlobByteArrayMessage>().ToPort(port);
        }).StartAsync();

        _receiver = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            Configure(opts);
            opts.ListenAtPort(port);
        }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _receiver.StopAsync();
        await _publisher.StopAsync();
        _receiver.Dispose();
        _publisher.Dispose();
    }

    [Fact]
    public async Task routed_message_uses_its_store_and_stamps_the_key()
    {
        var body = string.Join("\n", Enumerable.Repeat("routed", 200));

        var session = await _publisher.TrackActivity()
            .AlsoTrack(_receiver)
            .SendMessageAndWaitAsync(new BlobStringMessage("note", body));

        var received = session.Received.SingleMessage<BlobStringMessage>();
        received.Body.ShouldBe(body); // round trip through the routed store

        // The routed store saw it; the default store never did.
        _routed.StoreCount.ShouldBeGreaterThan(0);
        _routed.LoadCount.ShouldBeGreaterThan(0);
        _default.StoreCount.ShouldBe(0);

        var sent = session.Sent.SingleEnvelope<BlobStringMessage>();
        sent.Headers.TryGetValue(ClaimCheckHeaders.StoreHeaderName, out var key).ShouldBeTrue();
        key.ShouldNotBeNull();
        key.ShouldContain("BlobStringMessage");
    }

    [Fact]
    public async Task unrouted_message_uses_the_default_store_with_no_store_header()
    {
        var bytes = new byte[1024];
        Random.Shared.NextBytes(bytes);

        var session = await _publisher.TrackActivity()
            .AlsoTrack(_receiver)
            .SendMessageAndWaitAsync(new BlobByteArrayMessage("doc.pdf", bytes));

        var received = session.Received.SingleMessage<BlobByteArrayMessage>();
        received.Payload!.ShouldBe(bytes);

        // Default store handled it; the routed store stayed idle.
        _default.StoreCount.ShouldBeGreaterThan(0);
        _default.LoadCount.ShouldBeGreaterThan(0);
        _routed.StoreCount.ShouldBe(0);

        // Default-store envelopes carry no store-key header — byte-for-byte identical to pre-routing.
        var sent = session.Sent.SingleEnvelope<BlobByteArrayMessage>();
        sent.Headers.Keys.ShouldNotContain(ClaimCheckHeaders.StoreHeaderName);
    }
}
