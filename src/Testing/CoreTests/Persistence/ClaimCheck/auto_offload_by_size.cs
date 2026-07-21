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
/// Coverage for GH-3504: a configured size threshold auto-offloads the whole serialized body to the
/// store even when no property is decorated with <see cref="BlobAttribute"/>. This is the safety net
/// for the "forgot [Blob] on a large property and slammed into the broker's size limit" failure.
/// </summary>
public class auto_offload_by_size : IAsyncLifetime
{
    private const int Threshold = 1024;

    // A single shared store both nodes point at, so we can assert it actually saw the traffic.
    private readonly RecordingInMemoryClaimCheckStore _store = new();
    private IHost _publisher = null!;
    private IHost _receiver = null!;

    public async Task InitializeAsync()
    {
        CapturedMessages.Reset();

        var port = PortFinder.GetAvailablePort();

        _publisher = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(auto_offload_by_size).Assembly;

            opts.UseClaimCheck(c =>
            {
                c.Store = _store;
                c.AutoOffloadPayloadsLargerThan(Threshold);
            });

            opts.PublishMessage<PlainMessage>().ToPort(port);
        }).StartAsync();

        _receiver = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(auto_offload_by_size).Assembly;

            opts.UseClaimCheck(c =>
            {
                c.Store = _store;
                c.AutoOffloadPayloadsLargerThan(Threshold);
            });

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
    public async Task large_body_without_blob_attribute_is_offloaded_and_round_trips()
    {
        // PlainMessage has NO [Blob] property, but the payload pushes the serialized body over the
        // threshold, so the whole body must be off-loaded automatically.
        var bytes = new byte[4096];
        Random.Shared.NextBytes(bytes);

        var session = await _publisher.TrackActivity()
            .AlsoTrack(_receiver)
            .SendMessageAndWaitAsync(new PlainMessage("big", bytes));

        var sent = session.Sent.SingleEnvelope<PlainMessage>();
        sent.Headers.Keys.ShouldContain(ClaimCheckHeaders.BodyHeaderName);
        sent.Data!.Length.ShouldBe(0); // the real body left the wire

        var received = session.Received.SingleMessage<PlainMessage>();
        received.ShouldNotBeNull();
        received.Name.ShouldBe("big");
        received.InlineBytes.ShouldBe(bytes);

        _store.StoreCount.ShouldBeGreaterThan(0);
        _store.LoadCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task small_body_stays_inline()
    {
        var session = await _publisher.TrackActivity()
            .AlsoTrack(_receiver)
            .SendMessageAndWaitAsync(new PlainMessage("small", new byte[] { 1, 2, 3 }));

        var sent = session.Sent.SingleEnvelope<PlainMessage>();
        sent.Headers.Keys.ShouldNotContain(ClaimCheckHeaders.BodyHeaderName);
        sent.Data!.Length.ShouldBeGreaterThan(0); // body stayed on the wire

        var received = session.Received.SingleMessage<PlainMessage>();
        received.Name.ShouldBe("small");
        received.InlineBytes.ShouldBe(new byte[] { 1, 2, 3 });

        _store.StoreCount.ShouldBe(0);
    }
}
