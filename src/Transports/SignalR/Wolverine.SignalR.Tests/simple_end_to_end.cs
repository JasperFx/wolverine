using JasperFx.Core;
using Shouldly;
using Wolverine.SignalR.Client;
using Wolverine.Tracking;

namespace Wolverine.SignalR.Tests;

public class simple_end_to_end : WebSocketTestContext
{
    [Fact]
    public async Task send_message_from_hub_to_client()
    {
        using var client = await StartClientHost();

        var tracked = await theWebApp
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(client)
            .SendMessageAndWaitAsync(new FromFirst("Xavier Worthy"));

        var record = tracked.Received.SingleRecord<FromFirst>();
        record.ServiceName.ShouldBe("Client");
        record.Envelope.Message.ShouldBeOfType<FromFirst>()
            .Name.ShouldBe("Xavier Worthy");
    }

    #region sample_end_to_end_test_with_signalr

    [Fact]
    public async Task receive_message_from_a_client()
    {
        // This is an IHost that has the SignalR Client
        // transport configured to connect to a SignalR
        // server in the "theWebApp" IHost
        using var client = await StartClientHost();

        var tracked = await client
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(theWebApp)
            .Timeout(10.Seconds())
            .ExecuteAndWaitAsync(c => c.SendViaSignalRClient(clientUri, new ToSecond("Hollywood Brown")));

        var record = tracked.Received.SingleRecord<ToSecond>();
        record.ServiceName.ShouldBe("Server");
        record.Envelope.Destination.ShouldBe(new Uri("signalr://wolverine"));
        record.Message.ShouldBeOfType<ToSecond>()
            .Name.ShouldBe("Hollywood Brown");

    }

    #endregion
}
