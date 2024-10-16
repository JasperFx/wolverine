using Alba;
using Shouldly;
using Wolverine.Tracking;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class publishing_messages_from_middleware : IntegrationContext
{
    public publishing_messages_from_middleware(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task receive_messages_from_before_and_after_middleware()
    {
        Func<IMessageContext, Task> execute = async c =>
        {
            await Host.Scenario(x =>
            {
                x.Post.Url("/middleware-messages/leia");
            });
        };
        
        var tracked = await Host
            .TrackActivity()
            .WaitForMessageToBeReceivedAt<AfterMessage1>(Host)
            .ExecuteAndWaitAsync(execute);

        tracked.Received.SingleMessage<BeforeMessage1>()
            .Name.ShouldBe("leia");
        
        tracked.Received.SingleMessage<AfterMessage1>()
            .Name.ShouldBe("leia");
    }
}