using Azure.Messaging.ServiceBus.Administration;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// GH-5342. A service that listens to a topic another service publishes to wants Wolverine to provision its own
/// subscription and rule, but never the topic. Creating the topic first, with Wolverine's default options, fixes
/// settings such as partitioning and duplicate detection that its owner can then never change.
/// </summary>
public class externally_owned_topics_5342 : IAsyncLifetime
{
    private readonly ServiceBusAdministrationClient theAdmin =
        new(Servers.AzureServiceBusManagementConnectionString);

    public async ValueTask InitializeAsync()
    {
        // The first UseAzureServiceBusTesting() in a test process wipes the emulator. Get that out of the way here,
        // or it would delete the topics these tests create beforehand to stand in for their owners
        new WolverineOptions().UseAzureServiceBusTesting();

        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    [Fact]
    public async Task an_externally_owned_topic_is_not_changed_but_the_subscription_is_provisioned()
    {
        var ct = TestContext.Current.CancellationToken;

        // The publishing service owns the topic, and created it its own way
        await theAdmin.CreateTopicAsync(new CreateTopicOptions("orders")
        {
            DefaultMessageTimeToLive = 30.Minutes()
        }, ct);

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Wolverine still provisions everything this application owns...
                opts.UseAzureServiceBusTesting().AutoProvision();

                #region sample_asb_topic_is_externally_owned

                opts.ListenToAzureServiceBusSubscription("shipping")
                    .FromTopic("orders")

                    // The "orders" topic belongs to the service that publishes to it.
                    // Wolverine creates the "shipping" subscription and its rule on that
                    // topic, but never creates, changes or deletes the topic itself
                    .TopicIsExternallyOwned();

                #endregion
            }).StartAsync(ct);

        (await theAdmin.SubscriptionExistsAsync("orders", "shipping", ct)).Value.ShouldBeTrue();

        // Still the owner's topic, not one re-made with Wolverine's defaults
        var topic = await theAdmin.GetTopicAsync("orders", ct);
        topic.Value.DefaultMessageTimeToLive.ShouldBe(30.Minutes());
    }

    [Fact]
    public async Task a_missing_externally_owned_topic_is_named_and_not_created()
    {
        var ct = TestContext.Current.CancellationToken;

        var builder = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Give up after the first attempt rather than retrying for two minutes
                opts.BrokerInitializationTimeout = 1.Seconds();

                opts.UseAzureServiceBusTesting().AutoProvision();

                opts.ListenToAzureServiceBusSubscription("shipping")
                    .FromTopic("payments")
                    .TopicIsExternallyOwned();
            });

        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            using var host = await builder.StartAsync(ct);
        });

        var notFound = findInChain<ExternallyOwnedTopicNotFoundException>(ex);
        notFound.ShouldNotBeNull();
        notFound.TopicName.ShouldBe("payments");
        notFound.SubscriptionName.ShouldBe("shipping");
        notFound.Message.ShouldContain("'payments'");

        (await theAdmin.TopicExistsAsync("payments", ct)).Value.ShouldBeFalse();
    }

    [Fact]
    public async Task resources_setup_and_teardown_leave_externally_owned_objects_alone()
    {
        var ct = TestContext.Current.CancellationToken;

        await theAdmin.CreateTopicAsync("events", ct);
        await theAdmin.CreateTopicAsync("audit", ct);
        await theAdmin.CreateQueueAsync("inbox", ct);
        await theAdmin.CreateQueueAsync("billing", ct);

        IHostBuilder configure() => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting();

                opts.ListenToAzureServiceBusSubscription("reporting")
                    .FromTopic("events")
                    .TopicIsExternallyOwned();

                #region sample_asb_queues_and_topics_are_externally_owned

                // A topic another service owns and this one only publishes to
                opts.PublishMessage<AuditEntryRecorded>()
                    .ToAzureServiceBusTopic("audit")
                    .ExternallyOwned();

                // A queue another service owns and this one only reads from
                opts.ListenToAzureServiceBusQueue("inbox")
                    .ExternallyOwned();

                // A queue another service owns and this one only sends to
                opts.PublishMessage<InvoiceRequested>()
                    .ToAzureServiceBusQueue("billing")
                    .ExternallyOwned();

                #endregion

                // Owned by this application, so provisioned as usual
                opts.PublishMessage<ShipmentNotice>()
                    .ToAzureServiceBusQueue("notifications");
            });

        (await configure().RunJasperFxCommands(["resources", "setup"])).ShouldBe(0);

        (await theAdmin.SubscriptionExistsAsync("events", "reporting", ct)).Value.ShouldBeTrue();
        (await theAdmin.QueueExistsAsync("notifications", ct)).Value.ShouldBeTrue();

        // check still covers the externally owned objects, and they all exist
        (await configure().RunJasperFxCommands(["resources", "check"])).ShouldBe(0);

        (await configure().RunJasperFxCommands(["resources", "teardown"])).ShouldBe(0);

        // What this application owns is gone...
        (await theAdmin.SubscriptionExistsAsync("events", "reporting", ct)).Value.ShouldBeFalse();
        (await theAdmin.QueueExistsAsync("notifications", ct)).Value.ShouldBeFalse();

        // ...and what it doesn't is still there
        (await theAdmin.TopicExistsAsync("events", ct)).Value.ShouldBeTrue();
        (await theAdmin.TopicExistsAsync("audit", ct)).Value.ShouldBeTrue();
        (await theAdmin.QueueExistsAsync("inbox", ct)).Value.ShouldBeTrue();
        (await theAdmin.QueueExistsAsync("billing", ct)).Value.ShouldBeTrue();
    }

    private static T? findInChain<T>(Exception? ex) where T : Exception
    {
        while (ex != null)
        {
            if (ex is T found) return found;

            if (ex is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    var match = findInChain<T>(inner);
                    if (match != null) return match;
                }
            }

            ex = ex.InnerException;
        }

        return null;
    }
}

public record AuditEntryRecorded;
public record InvoiceRequested;
public record ShipmentNotice;
