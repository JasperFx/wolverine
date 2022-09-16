using System;
using System.Threading;
using System.Threading.Tasks;
using Baseline.Dates;
using Wolverine.Util;
using TestingSupport.Compliance;
using Xunit;

namespace Wolverine.Pulsar.Tests
{

    public class PulsarSendingFixture : SendingComplianceFixture, IAsyncLifetime
    {
        public PulsarSendingFixture() : base(null)
        {

        }

        public async Task InitializeAsync()
        {
            var topic = Guid.NewGuid().ToString();
            var topicPath = $"persistent://public/default/compliance{topic}";
            OutboundAddress = PulsarEndpoint.UriFor(topicPath);

            await SenderIs(opts =>
            {
                var listener = $"persistent://public/default/replies{topic}";
                opts.UsePulsar(e => {});
                opts.ListenToPulsarTopic(listener).UseForReplies();
            });

            await ReceiverIs(opts =>
            {
                opts.UsePulsar();
                opts.ListenToPulsarTopic(topicPath);
            });
        }

        public override void BeforeEach()
        {
            // A cooldown makes these tests far more reliable
            Thread.Sleep(3.Seconds());
        }

        public Task DisposeAsync()
        {

            return Task.CompletedTask;
        }
    }

    [Collection("acceptance")]
    public class PulsarSendingComplianceTests : SendingCompliance<PulsarSendingFixture>
    {

    }
}
