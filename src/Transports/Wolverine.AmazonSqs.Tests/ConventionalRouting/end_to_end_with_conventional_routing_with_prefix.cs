using Baseline.Dates;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Wolverine.Tracking;

namespace Wolverine.AmazonSqs.Tests.ConventionalRouting
{
    public class end_to_end_with_conventional_routing_with_prefix : IDisposable
    {
        private readonly IHost _sender;
        private readonly IHost _receiver;

        public end_to_end_with_conventional_routing_with_prefix()
        {
            _sender = WolverineHost.For(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .PrefixIdentifiers("shazaam")
                    .UseConventionalRouting().AutoProvision().AutoPurgeOnStartup();
                opts.Handlers.DisableConventionalDiscovery();
                opts.ServiceName = "Sender";
            });

            _receiver = WolverineHost.For(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .PrefixIdentifiers("shazaam")
                    .UseConventionalRouting().AutoProvision().AutoPurgeOnStartup();
                opts.ServiceName = "Receiver";
            });
        }

        public void Dispose()
        {
            _sender?.Dispose();
            _receiver?.Dispose();
        }

        [Fact]
        public async Task send_from_one_node_to_another_all_with_conventional_routing()
        {
            var session = await _sender.TrackActivity()
                .AlsoTrack(_receiver)
                .IncludeExternalTransports()
                .Timeout(30.Seconds())
                .SendMessageAndWaitAsync(new RoutedMessage());

            var received = session
                .AllRecordsInOrder()
                .Where(x => x.Envelope.Message?.GetType() == typeof(RoutedMessage))
                .Single(x => x.EventType == EventType.Received);

            received
                .ServiceName.ShouldBe("Receiver");
            
            received.Envelope.Destination
                .ShouldBe(new Uri("sqs://shazaam-routed/"));

        }
    }
}
