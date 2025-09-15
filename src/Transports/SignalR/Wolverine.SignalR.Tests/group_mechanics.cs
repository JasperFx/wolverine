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

public record EnrollMe(string GroupName) : WebSocketMessage;

public record KickMeOut(string GroupName) : WebSocketMessage;

public record BroadCastToGroup(string GroupName, string Message) : WebSocketMessage;

public record Information(string Message) : WebSocketMessage;

public static class GroupsHandler
{
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
    public static object Handle(BroadCastToGroup msg) 
        => new Information(msg.Message)
            .ToWebSocketGroup(msg.GroupName);

    public static void Handle(Information msg) 
        => Debug.WriteLine(msg.Message);

    [EnlistInCurrentConnectionSaga]
    public static DoMath Handle(AddNumbers numbers)
    {
        return new DoMath(numbers.X, numbers.Y);
    }

    public static MathAnswer Handle(DoMath math) => new MathAnswer(math.X + math.Y);

    public static void Handle(MathAnswer msg) => Debug.WriteLine(msg.Sum.ToString());
}

public record AddNumbers(int X, int Y);
public record DoMath(int X, int Y);
public record MathAnswer(int Sum);