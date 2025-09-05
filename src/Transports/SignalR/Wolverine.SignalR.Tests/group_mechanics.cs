using System.Diagnostics;
using Shouldly;
using Wolverine.Tracking;

namespace Wolverine.SignalR.Tests;

public class group_mechanics : WebSocketTestContext
{
    [Fact]
    public async Task enroll_and_send_to_group()
    {
        var green = await StartClientHost("green");
        var red = await StartClientHost("red");
        var blue = await StartClientHost("blue");

        await green.TrackActivity().IncludeExternalTransports().AlsoTrack(theWebApp)
            .SendMessageAndWaitAsync(new EnrollMe("Chiefs"));
        
        await blue.TrackActivity().IncludeExternalTransports().AlsoTrack(theWebApp)
            .SendMessageAndWaitAsync(new EnrollMe("Chiefs"));

        var tracked = await red.TrackActivity().AlsoTrack(green, blue, theWebApp)
            .IncludeExternalTransports()
            .SendMessageAndWaitAsync(new BroadCastToGroup("Chiefs", "Hello"));

        var records = tracked.Received.RecordsInOrder().Where(x => x.Message is Information).ToArray();
        
        records.Length.ShouldBe(2);
        records.Select(x => x.ServiceName).OrderBy(x => x).ShouldBe(["blue", "green"]);
        
        records
            .Select(x => x.Message)
            .OfType<Information>()
            .All(x => x.Message == "Hello")
            .ShouldBeTrue();
    }
}

public record EnrollMe(string GroupName) : WebSocketMessage;

public record KickMeOut(string GroupName) : WebSocketMessage;

public record BroadCastToGroup(string GroupName, string Message) : WebSocketMessage;

public record Information(string Message) : WebSocketMessage;

public static class GroupsHandler
{
    public static AddConnectionToGroup Handle(EnrollMe msg) 
        => new(msg.GroupName);

    public static RemoveConnectionToGroup Handle(KickMeOut msg) 
        => new(msg.GroupName);

    public static object Handle(BroadCastToGroup msg) 
        => new Information(msg.Message)
            .ToWebSocketGroup(msg.GroupName);

    public static void Handle(Information msg) 
        => Debug.WriteLine(msg.Message);
}