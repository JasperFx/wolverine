using JasperFx.Core;
using Shouldly;
using Wolverine.SignalR.Client;
using Wolverine.Tracking;

namespace Wolverine.SignalR.Tests;

// GH-5080: SignalR only carries the CloudEvents document, so custom headers have to be inside it
public class custom_headers_through_cloud_events : WebSocketTestContext
{
    private static DeliveryOptions withHeaders()
    {
        var options = new DeliveryOptions();
        options.Headers["x-user"] = "alice";
        options.Headers["x-country"] = "ca";
        return options;
    }

    [Fact]
    public async Task from_hub_to_client()
    {
        using var client = await StartClientHost();

        var tracked = await theWebApp
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(client)
            .SendMessageAndWaitAsync(new FromFirst("Xavier Worthy"), withHeaders());

        var envelope = tracked.Received.SingleEnvelope<FromFirst>();
        envelope.Headers["x-user"].ShouldBe("alice");
        envelope.Headers["x-country"].ShouldBe("ca");
    }

    [Fact]
    public async Task from_client_to_hub()
    {
        using var client = await StartClientHost();

        var tracked = await client
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(theWebApp)
            .Timeout(10.Seconds())
            .ExecuteAndWaitAsync(c => c
                .EndpointFor(SignalRClientEndpoint.TranslateToWolverineUri(clientUri))
                .SendAsync(new ToSecond("Hollywood Brown"), withHeaders()));

        var envelope = tracked.Received.SingleEnvelope<ToSecond>();
        envelope.Headers["x-user"].ShouldBe("alice");
        envelope.Headers["x-country"].ShouldBe("ca");
    }
}
