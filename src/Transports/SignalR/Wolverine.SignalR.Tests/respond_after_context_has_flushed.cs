using System.Diagnostics;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.SignalR.Tests;

public class respond_after_context_has_flushed : WebSocketTestContext
{
    // GH-3499 -- a handler whose MessageContext has already flushed (e.g. an
    // explicit SaveChangesAsync() on an outboxed session drains the context)
    // was silently dropping the ResponseToCallingWebSocket cascading response
    [Fact]
    public async Task response_is_still_delivered_when_the_context_already_flushed()
    {
        var green = await StartClientHost("green");
        var red = await StartClientHost("red");

        var tracked = await red.TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(theWebApp)
            .SendMessageAndWaitAsync(new RequiresResponseAfterFlush("Josh Allen"));

        var record = tracked.Executed.SingleRecord<ResponseAfterFlush>();

        // Verify that the response still went to the original calling client
        record.ServiceName.ShouldBe("red");
        record.Message.ShouldBeOfType<ResponseAfterFlush>().Name.ShouldBe("Josh Allen");
    }
}

public record RequiresResponseAfterFlush(string Name) : WebSocketMessage;
public record ResponseAfterFlush(string Name) : WebSocketMessage;

public static class ResponseAfterFlushHandler
{
    public static async Task<ResponseToCallingWebSocket<ResponseAfterFlush>> Handle(
        RequiresResponseAfterFlush msg, IMessageContext context)
    {
        // Simulate what an explicit SaveChangesAsync() on an outboxed session
        // does to the context: enqueue an outgoing message, then drain the
        // context so its OnlyOnce flush guard latches before the cascading
        // response is applied
        await context.PublishAsync(new FromFirst(msg.Name));
        await ((MessageContext)context).FlushOutgoingMessagesAsync();

        return new ResponseAfterFlush(msg.Name).RespondToCallingWebSocket();
    }

    public static void Handle(ResponseAfterFlush msg) => Debug.WriteLine(msg);
}
