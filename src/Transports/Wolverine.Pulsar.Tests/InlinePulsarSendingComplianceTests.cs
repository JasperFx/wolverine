using System;
using System.Threading;
using System.Threading.Tasks;
using Baseline.Dates;
using Wolverine.Util;
using TestingSupport.Compliance;
using Xunit;

namespace Wolverine.Pulsar.Tests
{
    public class InlinePulsarSendingFixture : SendingComplianceFixture, IAsyncLifetime
    {
        public InlinePulsarSendingFixture() : base(null)
        {

        }

        public async Task InitializeAsync()
        {
            var topic = Guid.NewGuid().ToString();
            var topicPath = $"persistent://public/default/{topic}";
            OutboundAddress = PulsarEndpoint.UriFor(topicPath);

            await ReceiverIs(opts =>
            {
                opts.UsePulsar();
                opts.ListenToPulsarTopic(topicPath).ProcessInline();
            });

            await SenderIs(opts =>
            {
                var replyPath = $"persistent://public/default/replies-{topic}";
                opts.UsePulsar();
                opts.ListenToPulsarTopic(replyPath).UseForReplies().ProcessInline();
                opts.PublishAllMessages().ToPulsarTopic(topicPath).SendInline();
            });
        }

        public override void BeforeEach()
        {
            // These tests are *far* more reliable with a cooldown
            Thread.Sleep(3.Seconds());
        }

        public Task DisposeAsync()
        {
            return Task.CompletedTask;
        }

    }


    [Collection("acceptance")]
    public class InlinePulsarSendingComplianceTests : SendingCompliance<InlinePulsarSendingFixture>
    {

    }

}
