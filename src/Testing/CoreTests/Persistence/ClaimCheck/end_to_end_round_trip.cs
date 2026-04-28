using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Persistence.ClaimCheck.Internal;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Persistence.ClaimCheck;

public class end_to_end_round_trip : IAsyncLifetime
{
    private IHost _publisher = null!;
    private IHost _receiver = null!;
    private string _claimCheckDirectory = null!;

    public async Task InitializeAsync()
    {
        CapturedMessages.Reset();

        var port = PortFinder.GetAvailablePort();
        _claimCheckDirectory = Path.Combine(Path.GetTempPath(), "wolverine-claim-check-tests-" + Guid.NewGuid().ToString("N"));

        _publisher = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseClaimCheck(c => c.UseFileSystem(_claimCheckDirectory));
            opts.PublishMessage<BlobByteArrayMessage>().ToPort(port);
            opts.PublishMessage<BlobStringMessage>().ToPort(port);
            opts.PublishMessage<MultiBlobMessage>().ToPort(port);
            opts.PublishMessage<PlainMessage>().ToPort(port);
        }).StartAsync();

        _receiver = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseClaimCheck(c => c.UseFileSystem(_claimCheckDirectory));
            opts.ListenAtPort(port);
        }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _receiver.StopAsync();
        await _publisher.StopAsync();

        try
        {
            if (Directory.Exists(_claimCheckDirectory))
            {
                Directory.Delete(_claimCheckDirectory, recursive: true);
            }
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    [Fact]
    public async Task round_trips_a_byte_array_blob_property()
    {
        var bytes = new byte[1024];
        Random.Shared.NextBytes(bytes);

        var session = await _publisher.TrackActivity()
            .AlsoTrack(_receiver)
            .SendMessageAndWaitAsync(new BlobByteArrayMessage("doc.pdf", bytes));

        var received = session.Received.SingleMessage<BlobByteArrayMessage>();
        received.ShouldNotBeNull();
        received.Name.ShouldBe("doc.pdf");
        received.Payload.ShouldNotBeNull();
        received.Payload!.ShouldBe(bytes);
    }

    [Fact]
    public async Task round_trips_a_string_blob_property()
    {
        var body = string.Join("\n", Enumerable.Repeat("hello, world", 200));

        var session = await _publisher.TrackActivity()
            .AlsoTrack(_receiver)
            .SendMessageAndWaitAsync(new BlobStringMessage("note", body));

        var received = session.Received.SingleMessage<BlobStringMessage>();
        received.ShouldNotBeNull();
        received.Title.ShouldBe("note");
        received.Body.ShouldBe(body);
    }

    [Fact]
    public async Task round_trips_multiple_blob_properties()
    {
        var image = new byte[2048];
        Random.Shared.NextBytes(image);
        var notes = new string('z', 4096);

        var session = await _publisher.TrackActivity()
            .AlsoTrack(_receiver)
            .SendMessageAndWaitAsync(new MultiBlobMessage("multi", image, notes));

        var received = session.Received.SingleMessage<MultiBlobMessage>();
        received.ShouldNotBeNull();
        received.Description.ShouldBe("multi");
        received.Image.ShouldNotBeNull();
        received.Image!.ShouldBe(image);
        received.Notes.ShouldBe(notes);
    }

    [Fact]
    public async Task message_without_blob_properties_is_unmodified()
    {
        var inline = new byte[] { 1, 2, 3 };

        var session = await _publisher.TrackActivity()
            .AlsoTrack(_receiver)
            .SendMessageAndWaitAsync(new PlainMessage("plain", inline));

        var sent = session.Sent.SingleEnvelope<PlainMessage>();
        sent.Headers.Keys.ShouldNotContain(k => k.StartsWith(ClaimCheckHeaders.Prefix, StringComparison.Ordinal));

        var received = session.Received.SingleMessage<PlainMessage>();
        received.ShouldNotBeNull();
        received.Name.ShouldBe("plain");
        received.InlineBytes.ShouldBe(inline);
    }
}
