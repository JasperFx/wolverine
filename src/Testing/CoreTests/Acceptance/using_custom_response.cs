using System.Diagnostics;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

public class using_custom_response : IntegrationContext
{
    public using_custom_response(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public async Task use_synchronous_response()
    {
        var session = await Host.InvokeMessageAndWaitAsync(new SendTag1("blue"));
        session.Received.SingleMessage<TaggedMessage>()
            .Tag.ShouldBe("blue");
    }

    [Fact]
    public async Task use_response_that_returns_Task()
    {
        var session = await Host.InvokeMessageAndWaitAsync(new SendTag2("green"));
        session.Received.SingleMessage<TaggedMessage>()
            .Tag.ShouldBe("green");
    }
    
    [Fact]
    public async Task use_response_that_returns_ValueTask()
    {
        var session = await Host.InvokeMessageAndWaitAsync(new SendTag3("red"));
        session.Received.SingleMessage<TaggedMessage>()
            .Tag.ShouldBe("red");
    }
    
    [Fact]
    public async Task use_synchronous_response_expect_response()
    {
        var (session, message) = await Host.InvokeMessageAndWaitAsync<TaggedMessage>(new SendTag1("blue"));
        message.Tag.ShouldBe("blue");
    }

    [Fact]
    public async Task use_response_that_returns_Task_expect_response()
    {
        var (session, message) = await Host.InvokeMessageAndWaitAsync<TaggedMessage>(new SendTag2("green"));
        message.Tag.ShouldBe("green");
    }
    
    [Fact]
    public async Task use_response_that_returns_ValueTask_expect_response()
    {
        var (session, message) = await Host.InvokeMessageAndWaitAsync<TaggedMessage>(new SendTag3("purple"));
        message.Tag.ShouldBe("purple");
    }
}

public record SendTag1(string Tag);
public record SendTag2(string Tag);
public record SendTag3(string Tag);

public static class SendTagHandler
{
    public static TaggedResponse Handle(SendTag1 message) => new TaggedResponse(message.Tag);
    public static AsyncTaggedResponse Handle(SendTag2 message) => new AsyncTaggedResponse(message.Tag);
    public static ValueTaskTaggedResponse Handle(SendTag3 message) => new ValueTaskTaggedResponse(message.Tag);

    public static void Handle(TaggedMessage message) => Debug.WriteLine("Got tag " + message.Tag);

}

public record TaggedMessage(string Tag);

public class TaggedResponse(string Tag) : IResponse
{
    public TaggedMessage Build() => new TaggedMessage(Tag);
}

public class AsyncTaggedResponse(string Tag) : IResponse
{
    public Task<TaggedMessage> Build() => Task.FromResult(new TaggedMessage(Tag));
}

public class ValueTaskTaggedResponse(string Tag) : IResponse
{
    public ValueTask<TaggedMessage> Build() => new ValueTask<TaggedMessage>(new TaggedMessage(Tag));
}



