using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

public class publishing_ISendMyself_messages : IntegrationContext
{
    public publishing_ISendMyself_messages(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public async Task assert_can_not_send_with_isendmyself()
    {
        var selfSender = new SelfSender(Guid.NewGuid());
        var tracked = await Host.SendMessageAndWaitAsync(selfSender);
        tracked.Executed.SingleMessage<Cascaded>().Id.ShouldBe(selfSender.Id);
    }
    
    [Fact]
    public async Task assert_can_not_publish_with_isendmyself()
    {
        var selfSender = new SelfSender(Guid.NewGuid());
        var tracked = await Host.ExecuteAndWaitValueTaskAsync(c => c.PublishAsync(selfSender));
        tracked.Executed.SingleMessage<Cascaded>().Id.ShouldBe(selfSender.Id);
    }
}