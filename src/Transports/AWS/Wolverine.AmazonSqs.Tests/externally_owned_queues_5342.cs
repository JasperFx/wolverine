using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Tests;

/// <summary>
/// GH-5342. An application that reads from or sends to an SQS queue another service owns wants Wolverine to
/// provision its own queues, but never to create, re-create or delete that one.
/// </summary>
public class externally_owned_queues_5342 : IAsyncLifetime
{
    private const string OwnersInbox = "gh5342-owners-inbox";
    private const string OwnersBilling = "gh5342-owners-billing";
    private const string OwnQueue = "gh5342-own-notifications";

    private readonly AmazonSQSClient theClient = new(new BasicAWSCredentials("ignore", "ignore"),
        new AmazonSQSConfig { ServiceURL = "http://localhost:4566" });

    public async ValueTask InitializeAsync()
    {
        await deleteAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await deleteAllAsync();
        theClient.Dispose();
    }

    [Fact]
    public async Task an_externally_owned_queue_is_used_as_its_owner_created_it()
    {
        var ct = TestContext.Current.CancellationToken;

        // The owning service created the queue its own way
        await theClient.CreateQueueAsync(new CreateQueueRequest(OwnersInbox)
        {
            Attributes = new Dictionary<string, string> { [QueueAttributeName.VisibilityTimeout] = "45" }
        }, ct);

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Wolverine still provisions everything this application owns...
                opts.UseAmazonSqsTransportLocally().AutoProvision();

                #region sample_sqs_queue_is_externally_owned

                // The queue belongs to another service. Wolverine reads from it, but
                // never creates, re-creates or deletes it, even with AutoProvision() on
                opts.ListenToSqsQueue("gh5342-owners-inbox")
                    .ExternallyOwned();

                #endregion

                opts.PublishMessage<ExternallyOwnedQueueMessage>().ToSqsQueue(OwnersInbox);
            }).StartAsync(ct);

        var session = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new ExternallyOwnedQueueMessage("hello"));

        session.Received.SingleMessage<ExternallyOwnedQueueMessage>().Name.ShouldBe("hello");

        // Still the owner's queue, not one re-made with Wolverine's defaults
        var url = (await theClient.GetQueueUrlAsync(OwnersInbox, ct)).QueueUrl;
        var attributes = await theClient.GetQueueAttributesAsync(url, [QueueAttributeName.VisibilityTimeout], ct);
        attributes.VisibilityTimeout.ShouldBe(45);
    }

    [Fact]
    public async Task resources_setup_and_teardown_leave_externally_owned_queues_alone()
    {
        var ct = TestContext.Current.CancellationToken;

        await theClient.CreateQueueAsync(OwnersInbox, ct);
        await theClient.CreateQueueAsync(OwnersBilling, ct);

        IHostBuilder configure() => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally();

                // A queue another service owns and this one only reads from
                opts.ListenToSqsQueue(OwnersInbox).ExternallyOwned().DisableDeadLetterQueueing();

                #region sample_sqs_publish_to_externally_owned_queue

                // A queue another service owns and this one only sends to
                opts.PublishMessage<InvoiceRequested>()
                    .ToSqsQueue("gh5342-owners-billing")
                    .ExternallyOwned();

                #endregion

                // Owned by this application, so provisioned as usual
                opts.PublishMessage<ShipmentNotice>().ToSqsQueue(OwnQueue);
            });

        (await configure().RunJasperFxCommands(["resources", "setup"])).ShouldBe(0);
        (await queueExistsAsync(OwnQueue)).ShouldBeTrue();

        // check still covers the externally owned queues, and they all exist
        (await configure().RunJasperFxCommands(["resources", "check"])).ShouldBe(0);

        (await configure().RunJasperFxCommands(["resources", "teardown"])).ShouldBe(0);

        // What this application owns is gone...
        (await queueExistsAsync(OwnQueue)).ShouldBeFalse();

        // ...and what it doesn't is still there
        (await queueExistsAsync(OwnersInbox)).ShouldBeTrue();
        (await queueExistsAsync(OwnersBilling)).ShouldBeTrue();
    }

    [Fact]
    public async Task a_missing_externally_owned_queue_is_not_created_at_startup()
    {
        var builder = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Give up after the first attempt rather than retrying
                opts.BrokerInitializationTimeout = 1.Seconds();

                opts.UseAmazonSqsTransportLocally().AutoProvision();

                opts.PublishMessage<InvoiceRequested>().ToSqsQueue(OwnersBilling).ExternallyOwned();
            });

        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            using var host = await builder.StartAsync(TestContext.Current.CancellationToken);
        });

        // The error names the queue and says why Wolverine did not create it
        var notFound = findInChain<ExternallyOwnedObjectNotFoundException>(ex);
        notFound.ShouldNotBeNull();
        notFound.ObjectName.ShouldBe(OwnersBilling);
        notFound.Message.ShouldContain($"'{OwnersBilling}'");
        notFound.Message.ShouldContain("externally owned");

        (await queueExistsAsync(OwnersBilling)).ShouldBeFalse();
    }

    private static T? findInChain<T>(Exception? ex) where T : Exception
    {
        while (ex != null)
        {
            if (ex is T found) return found;
            ex = ex.InnerException;
        }

        return null;
    }

    private async Task<bool> queueExistsAsync(string name)
    {
        try
        {
            await theClient.GetQueueUrlAsync(name);
            return true;
        }
        catch (QueueDoesNotExistException)
        {
            return false;
        }
    }

    private async Task deleteAllAsync()
    {
        foreach (var name in new[] { OwnersInbox, OwnersBilling, OwnQueue })
        {
            try
            {
                var url = (await theClient.GetQueueUrlAsync(name)).QueueUrl;
                await theClient.DeleteQueueAsync(url);
            }
            catch (QueueDoesNotExistException)
            {
                // Already gone
            }
        }
    }
}

public record ExternallyOwnedQueueMessage(string Name);

public static class ExternallyOwnedQueueMessageHandler
{
    public static void Handle(ExternallyOwnedQueueMessage message)
    {
    }
}

public record InvoiceRequested;
public record ShipmentNotice;
