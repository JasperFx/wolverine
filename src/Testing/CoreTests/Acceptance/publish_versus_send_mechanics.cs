using System.Linq;
using System.Threading.Tasks;
using JasperFx.Core;
using TestingSupport;
using TestingSupport.Compliance;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Acceptance;

public class publish_versus_send_mechanics : IntegrationContext
{
    public publish_versus_send_mechanics(DefaultApp @default) : base(@default)
    {
        with(opts =>
        {
            opts.DisableConventionalDiscovery();

            opts.Publish(x => x
                .Message<Message1>()
                .Message<Message2>()
                .ToLocalQueue("one"));

            opts.Publish(x => x.Message<Message2>().ToLocalQueue("two"));
        });
    }

    [Fact]
    public async Task publish_message_with_no_known_subscribers()
    {
        var session = await Host.ExecuteAndWaitValueTaskAsync(x => x.PublishAsync(new Message3()));

        session.AllRecordsInOrder().Any(x => x.MessageEventType != MessageEventType.NoRoutes).ShouldBeFalse();
    }

    [Fact]
    public async Task publish_with_known_subscribers()
    {
        var session = await Host.ExecuteAndWaitAsync(async c =>
        {
            await c.PublishAsync(new Message1());
            await c.PublishAsync(new Message2());
        });

        session
            .FindEnvelopesWithMessageType<Message1>(MessageEventType.Sent)
            .Single()
            .Envelope.Destination
            .ShouldBe("local://one".ToUri());

        session
            .FindEnvelopesWithMessageType<Message2>(MessageEventType.Sent)
            .Select(x => x.Envelope.Destination).OrderBy(x => x.ToString())
            .ShouldHaveTheSameElementsAs("local://one".ToUri(), "local://two".ToUri());
    }

    [Fact]
    public async Task send_message_with_no_known_subscribers()
    {
        await Should.ThrowAsync<IndeterminateRoutesException>(async () =>
            await Publisher.SendAsync(new Message3()));
    }

    [Fact]
    public async Task send_with_known_subscribers()
    {
        var session = await Host.ExecuteAndWaitAsync(async c =>
        {
            await c.SendAsync(new Message1());
            await c.SendAsync(new Message2());
        });

        session.AllRecordsInOrder(MessageEventType.Sent)
            .Length.ShouldBe(3);
    }
}