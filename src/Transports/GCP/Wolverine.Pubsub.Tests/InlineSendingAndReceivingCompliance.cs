using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class InlineComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public InlineComplianceFixture() : base(new Uri($"{PubsubTransport.ProtocolName}://wolverine/inline-receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        var id = Guid.NewGuid().ToString();

        OutboundAddress = new Uri($"{PubsubTransport.ProtocolName}://wolverine/inline-receiver.{id}");

        await SenderIs(opts =>
        {
            opts
                .UsePubsubTesting()
                .AutoProvision()
                .AutoPurgeOnStartup()
                .EnableDeadLettering()
                .EnableSystemEndpoints();

            opts
                .PublishAllMessages()
                .To(OutboundAddress)
                .SendInline();
        });

        await ReceiverIs(opts =>
        {
            opts
                .UsePubsubTesting()
                .AutoProvision()
                .AutoPurgeOnStartup()
                .EnableDeadLettering()
                .EnableSystemEndpoints();

            opts
                .ListenToPubsubTopic($"inline-receiver.{id}")
                .ProcessInline();
        });
    }

    public new async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

[Collection("acceptance")]
public class InlineSendingAndReceivingCompliance : TransportCompliance<InlineComplianceFixture>
{
    [Fact]
    public virtual async Task dl_mechanics()
    {
        throwOnAttempt<DivideByZeroException>(1);
        throwOnAttempt<DivideByZeroException>(2);
        throwOnAttempt<DivideByZeroException>(3);

        await shouldMoveToErrorQueueOnAttempt(1);

        var runtime = theReceiver.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<PubsubTransport>();
        var dl = transport.Topics[PubsubTransport.DeadLetterName];

        await dl.InitializeAsync(NullLogger.Instance);

        var pullResponse = await transport.SubscriberApiClient!.PullAsync(
            dl.Server.Subscription.Name,
            1
        );

        pullResponse.ReceivedMessages.ShouldNotBeEmpty();
    }
}