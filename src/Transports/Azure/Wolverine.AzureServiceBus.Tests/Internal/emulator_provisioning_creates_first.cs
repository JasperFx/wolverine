using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Internal;

/// <summary>
/// On the emulator, asking whether a missing entity exists takes 0.7 to 1 second, a create about 0.25, and a create of an
/// entity that already exists (409) a few milliseconds. So provisioning against the emulator creates first and takes
/// "already exists" as the answer, and provisioning against a real namespace keeps checking for existence first
/// </summary>
public class emulator_provisioning_creates_first
{
    private readonly ServiceBusAdministrationClient theClient = Substitute.For<ServiceBusAdministrationClient>();

    private static ServiceBusException alreadyExists()
        => new("409 Conflict", ServiceBusFailureReason.MessagingEntityAlreadyExists);

    [Theory]
    [InlineData("Endpoint=sb://localhost:5673;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;", true)]
    [InlineData("Endpoint=sb://localhost:5300;UseDevelopmentEmulator = TRUE", true)]
    [InlineData("Endpoint=sb://localhost:5673;UseDevelopmentEmulator=false;", false)]
    [InlineData("Endpoint=sb://real.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=abc=", false)]
    [InlineData(null, false)]
    public void recognizes_the_emulator_from_the_connection_string(string? connectionString, bool isEmulator)
    {
        AzureServiceBusTransport.IsEmulatorConnectionString(connectionString).ShouldBe(isEmulator);
    }

    [Fact]
    public void the_management_connection_string_decides_when_there_is_one()
    {
        var transport = new AzureServiceBusTransport
        {
            ConnectionString = "Endpoint=sb://real.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=y",
            ManagementConnectionString = "Endpoint=sb://localhost:5300;UseDevelopmentEmulator=true;"
        };

        transport.TargetsEmulator.ShouldBeTrue();
    }

    [Fact]
    public async Task an_emulator_queue_is_created_without_an_existence_check()
    {
        var queue = new AzureServiceBusQueue(new AzureServiceBusTransport(), "incoming");

        (await queue.SetupAsync(theClient, createFirst: true)).ShouldBeTrue();

        await theClient.DidNotReceive().QueueExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await theClient.Received().CreateQueueAsync(Arg.Is<CreateQueueOptions>(x => x.Name == "incoming"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task an_emulator_queue_that_already_exists_is_not_reported_as_created()
    {
        theClient.CreateQueueAsync(Arg.Any<CreateQueueOptions>(), Arg.Any<CancellationToken>()).ThrowsAsync(alreadyExists());

        var queue = new AzureServiceBusQueue(new AzureServiceBusTransport(), "incoming");

        (await queue.SetupAsync(theClient, createFirst: true)).ShouldBeFalse();
    }

    [Fact]
    public async Task a_real_namespace_queue_that_exists_is_not_created_again()
    {
        theClient.QueueExistsAsync("incoming", Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(true, Substitute.For<Response>()));

        var queue = new AzureServiceBusQueue(new AzureServiceBusTransport(), "incoming");

        (await queue.SetupAsync(theClient, createFirst: false)).ShouldBeFalse();

        await theClient.DidNotReceive().CreateQueueAsync(Arg.Any<CreateQueueOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task a_real_namespace_queue_that_is_missing_is_created()
    {
        theClient.QueueExistsAsync("incoming", Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(false, Substitute.For<Response>()));

        var queue = new AzureServiceBusQueue(new AzureServiceBusTransport(), "incoming");

        (await queue.SetupAsync(theClient, createFirst: false)).ShouldBeTrue();

        await theClient.Received().CreateQueueAsync(Arg.Is<CreateQueueOptions>(x => x.Name == "incoming"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task an_emulator_topic_is_created_without_an_existence_check_and_409_is_quiet()
    {
        theClient.CreateTopicAsync(Arg.Any<CreateTopicOptions>(), Arg.Any<CancellationToken>()).ThrowsAsync(alreadyExists());

        var topic = new AzureServiceBusTopic(new AzureServiceBusTransport(), "outgoing");

        await topic.SetupAsync(theClient, NullLogger.Instance, createFirst: true);

        await theClient.DidNotReceive().TopicExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await theClient.Received().CreateTopicAsync(Arg.Is<CreateTopicOptions>(x => x.Name == "outgoing"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task an_emulator_subscription_is_created_without_an_existence_check()
    {
        var transport = new AzureServiceBusTransport();
        var subscription = new AzureServiceBusSubscription(transport, new AzureServiceBusTopic(transport, "foo"), "bar");

        (await subscription.SetupAsync(theClient, NullLogger.Instance, createFirst: true)).ShouldBeTrue();

        await theClient.DidNotReceive().SubscriptionExistsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await theClient.Received().CreateSubscriptionAsync(
            Arg.Is<CreateSubscriptionOptions>(x => x.TopicName == "foo" && x.SubscriptionName == "bar"),
            Arg.Any<CreateRuleOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task an_emulator_subscription_that_already_exists_has_its_rules_reconciled()
    {
        theClient.CreateSubscriptionAsync(Arg.Any<CreateSubscriptionOptions>(), Arg.Any<CreateRuleOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(alreadyExists());
        theClient.GetRulesAsync("foo", "bar", Arg.Any<CancellationToken>())
            .Returns(AsyncPageable<RuleProperties>.FromPages([
                Page<RuleProperties>.FromValues([], null, Substitute.For<Response>())
            ]));

        var transport = new AzureServiceBusTransport();
        var subscription = new AzureServiceBusSubscription(transport, new AzureServiceBusTopic(transport, "foo"), "bar");

        (await subscription.SetupAsync(theClient, NullLogger.Instance, createFirst: true)).ShouldBeFalse();

        // The same path an existence check that answered "yes" takes
        theClient.Received().GetRulesAsync("foo", "bar", Arg.Any<CancellationToken>());
    }
}
