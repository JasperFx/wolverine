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

    [Fact]
    public async Task take_advantage_of_the_enlist_in_current_connection_saga()
    {
        var green = await StartClientHost("green");
        var red = await StartClientHost("red");
        var blue = await StartClientHost("blue");

        var tracked = await green.TrackActivity().IncludeExternalTransports().AlsoTrack(theWebApp)
            .WaitForMessageToBeReceivedAt<MathAnswer>(green)
            .SendMessageAndWaitAsync(new AddNumbers(5,6));

        var record = tracked.Executed.SingleRecord<MathAnswer>();
        record.ServiceName.ShouldBe("green");
        record.Envelope.Message.ShouldBeOfType<MathAnswer>().Sum.ShouldBe(11);

    }
}

#region sample_messages_related_to_signalr_groups

public record EnrollMe(string GroupName) : WebSocketMessage;

public record KickMeOut(string GroupName) : WebSocketMessage;

public record BroadCastToGroup(string GroupName, string Message) : WebSocketMessage;

#endregion

public record Information(string Message) : WebSocketMessage;

public static class GroupsHandler
{
    #region sample_group_mechanics_with_signalr

    // Declaring that you need the connection that originated
    // this message to be added to the named SignalR client group
    public static AddConnectionToGroup Handle(EnrollMe msg) 
        => new(msg.GroupName);

    // Declaring that you need the connection that originated this
    // message to be removed from the named SignalR client group
    public static RemoveConnectionToGroup Handle(KickMeOut msg) 
        => new(msg.GroupName);

    // The message wrapper here sends the raw message to
    // the named SignalR client group
    public static SignalRMessage<Information> Handle(BroadCastToGroup msg) 
        => new Information(msg.Message)
            // This extension method wraps the "real" message 
            // with an envelope that routes this original message
            // to the named group
            .ToWebSocketGroup(msg.GroupName);

    #endregion

    public static void Handle(Information msg) 
        => Debug.WriteLine(msg.Message);

    #region sample_enlist_in_current_connection_saga

    // Directs Wolverine to track the connection id that
    // originated this incoming message so that any
    // resulting SignalR messages as a response to this
    // original message are sent to only the originating connection
    [EnlistInCurrentConnectionSaga]
    public static DoMath Handle(AddNumbers numbers)
    {
        return new DoMath(numbers.X, numbers.Y);
    }

    #endregion

    public static MathAnswer Handle(DoMath math) => new MathAnswer(math.X + math.Y);

    public static void Handle(MathAnswer msg) => Debug.WriteLine(msg.Sum.ToString());
}

public record AddNumbers(int X, int Y);
public record DoMath(int X, int Y);
public record MathAnswer(int Sum);