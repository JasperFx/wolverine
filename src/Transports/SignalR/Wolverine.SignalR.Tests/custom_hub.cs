using JasperFx.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.SignalR.Client;
using Wolverine.SignalR.Internals;
using Wolverine.Tracking;

namespace Wolverine.SignalR.Tests;

public class custom_hub : WebSocketTestContextWithCustomHub<CustomWolverineHub>
{
    public static List<string> ReceivedJson { get; } = new();

    public override async Task DisposeAsync()
    {
        ReceivedJson.Clear();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task publishing_from_server_uses_custom_hub()
    {
        var client = await StartClientHost();

        var tracked = await theWebApp
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(client)
            .Timeout(10.Seconds())
            .SendMessageAndWaitAsync(new FromSecond("Hollywood Brown"));

        var record = tracked.Received.SingleRecord<FromSecond>();
        record.ServiceName.ShouldBe("Client");
        record.Envelope.ShouldNotBeNull();
        record.Envelope.Destination.ShouldBe(new Uri($"signalr-client://localhost:{Port}/messages"));
        record.Message.ShouldBeOfType<FromSecond>()
            .Name.ShouldBe("Hollywood Brown");
    }

    [Fact]
    public async Task publishing_from_client_uses_custom_hub()
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
        record.Envelope.ShouldNotBeNull();
        record.Envelope.Destination.ShouldBe(new Uri("signalr://wolverine"));
        record.Message.ShouldBeOfType<ToSecond>()
            .Name.ShouldBe("Hollywood Brown");

        ReceivedJson.Count.ShouldBe(1);
    }
}

public class custom_hub_with_authentication : WebSocketTestContextWithCustomHub<AuthenticatedWolverineHub>
{
    public static List<string> ReceivedJson { get; } = new();

    public override async Task DisposeAsync()
    {
        ReceivedJson.Clear();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task client_can_receive_from_authenticated_hub()
    {
        var client = await StartClientHost();

        var tracked = await theWebApp
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(client)
            .Timeout(10.Seconds())
            .SendMessageAndWaitAsync(new FromSecond("Hollywood Brown"));

        var record = tracked.Received.SingleRecord<FromSecond>();
        record.ServiceName.ShouldBe("Client");
        record.Envelope.ShouldNotBeNull();
        record.Envelope.Destination.ShouldBe(new Uri($"signalr-client://localhost:{Port}/messages"));
        record.Message.ShouldBeOfType<FromSecond>()
            .Name.ShouldBe("Hollywood Brown");
    }

    [Fact]
    public async Task client_can_publish_using_authenticated_hub()
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
        record.Envelope.ShouldNotBeNull();
        record.Envelope.Destination.ShouldBe(new Uri("signalr://wolverine"));
        record.Message.ShouldBeOfType<ToSecond>()
            .Name.ShouldBe("Hollywood Brown");

        ReceivedJson.Count.ShouldBe(1);
    }

    [Fact]
    public async Task client_with_invalid_token_cannot_connect()
    {
        // This is an IHost that has the SignalR Client
        // transport configured to connect to a SignalR
        // server in the "theWebApp" IHost
        using var client = await StartClientHost(accessToken: "last-years-token");

        var tracked = await client
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(theWebApp)
            .Timeout(10.Seconds())
            .ExecuteAndWaitAsync(c => c.SendViaSignalRClient(clientUri, new ToSecond("Hollywood Brown")));

        tracked.Received.Envelopes().ShouldBeEmpty();
        ReceivedJson.ShouldBeEmpty();
    }

    [Fact]
    public async Task client_with_invalid_token_cannot_receive()
    {
        var client = await StartClientHost(accessToken: "last-years-token");

        var tracked = await theWebApp
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(client)
            .Timeout(100.Milliseconds())
            .DoNotAssertOnExceptionsDetected() // We're not supposed to be able to receive, so don't throw
            .PublishMessageAndWaitAsync(new FromSecond("Hollywood Brown"));

        tracked.Received.Envelopes().ShouldBeEmpty();
        ReceivedJson.ShouldBeEmpty();
    }
}

public class CustomWolverineHub(SignalRTransport endpoint, ILogger<CustomWolverineHub> logger) : WolverineHub(endpoint)
{
    public override Task OnConnectedAsync()
    {
        logger.LogInformation("Client connected with ID {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task ReceiveMessage(string json)
    {
        custom_hub.ReceivedJson.Add(json);
        return base.ReceiveMessage(json);
    }
}

[Authorize]
public class AuthenticatedWolverineHub(SignalRTransport endpoint, ILogger<CustomWolverineHub> logger) : WolverineHub(endpoint)
{
    public override Task OnConnectedAsync()
    {
        logger.LogInformation("Client authenticated with ID {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task ReceiveMessage(string json)
    {
        custom_hub_with_authentication.ReceivedJson.Add(json);
        return base.ReceiveMessage(json);
    }
}
