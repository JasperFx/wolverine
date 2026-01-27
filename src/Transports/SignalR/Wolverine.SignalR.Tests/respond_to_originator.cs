using System.Diagnostics;
using Shouldly;
using Wolverine.Tracking;

namespace Wolverine.SignalR.Tests;

public class respond_to_originator : WebSocketTestContext
{
    [Fact]
    public async Task send_to_the_originating_connection()
    {
        var green = await StartClientHost("green");
        var red = await StartClientHost("red");
        var blue = await StartClientHost("blue");

        var tracked = await red.TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(theWebApp)
            .SendMessageAndWaitAsync(new RequiresResponse("Leo Chenal"));

        var record = tracked.Executed.SingleRecord<WebSocketResponse>();
        
        // Verify that the response went to the original calling client
        record.ServiceName.ShouldBe("red");
        record.Message.ShouldBeOfType<WebSocketResponse>().Name.ShouldBe("Leo Chenal");
    }
}

public record RequiresResponse(string Name) : WebSocketMessage;
public record WebSocketResponse(string Name) : WebSocketMessage;



public static class ResponseHandler
{
    public static ResponseToCallingWebSocket<WebSocketResponse> Handle(RequiresResponse msg) 
        => new WebSocketResponse(msg.Name).RespondToCallingWebSocket();

    public static void Handle(WebSocketResponse msg) => Debug.WriteLine(msg);
}