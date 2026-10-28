using System.Collections.Concurrent;
using System.Text.Json;
using System.Web;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace Wolverine.AmazonSqs.Tests;

public record ForeignOrderPlaced(string OrderNumber);

public static class ForeignOrderPlacedHandler
{
    private static readonly ConcurrentDictionary<string, int> _handled = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> _waiters = new();

    public static void Handle(ForeignOrderPlaced message)
    {
        _handled.AddOrUpdate(message.OrderNumber, 1, (_, count) => count + 1);
        waiterFor(message.OrderNumber).TrySetResult();
    }

    public static int TimesHandled(string orderNumber) => _handled.GetValueOrDefault(orderNumber);

    public static Task WaitForAsync(string orderNumber) => waiterFor(orderNumber).Task.WaitAsync(60.Seconds());

    private static TaskCompletionSource waiterFor(string orderNumber) =>
        _waiters.GetOrAdd(orderNumber, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}

/// <summary>
/// The durable inbox deduplicates on <see cref="Envelope.Id" />, and
/// <see cref="DurabilitySettings.KeepAfterMessageHandling" /> keeps a handled row around to absorb a broker
/// redelivery. A message from a producer that is not Wolverine carries no Wolverine id, so the id it is stored
/// under has to come from the broker's identity of the message. Otherwise every delivery of it is a brand new
/// envelope, stored again and handled again.
///
/// <para>
/// Two ways SQS hands a durable listener the same message twice:
/// </para>
/// <list type="bullet">
/// <item>
/// The delete after the inbox insert never lands -- the node died right after the insert, or the DeleteMessage
/// call was lost -- so the message becomes visible again at the visibility timeout, under the same SQS message id.
/// </item>
/// <item>
/// SNS delivers one notification to the queue twice. It is at-least-once too, and each delivery is a separate SQS
/// message with its own SQS message id. Only the notification's own MessageId is the same on both.
/// </item>
/// </list>
/// </summary>
public class redelivered_message_without_wolverine_id_is_handled_once
{
    public enum MessageShape
    {
        // A raw JSON body read by ReceiveRawJsonMessage(): no Wolverine metadata at all
        RawJson,

        // The same raw JSON delivered by an SNS subscription without raw message delivery, read by
        // ReceiveSnsTopicMessage()
        RawJsonInAnSnsNotification
    }

    public enum Redelivery
    {
        AfterTheDeleteWasLost,
        SnsDeliveredTheNotificationTwice
    }

    private const string Schema = "sqs_foreign_redelivery";

    [Theory]
    [InlineData(MessageShape.RawJson, Redelivery.AfterTheDeleteWasLost)]
    [InlineData(MessageShape.RawJsonInAnSnsNotification, Redelivery.AfterTheDeleteWasLost)]
    [InlineData(MessageShape.RawJsonInAnSnsNotification, Redelivery.SnsDeliveredTheNotificationTwice)]
    public async Task a_redelivered_message_is_handled_once(MessageShape shape, Redelivery redelivery)
    {
        var run = new TestRun(shape);

        // Loses the listener's first delete on this queue, as a node that died right after the inbox insert would
        var client = new DeleteLosingSqsClient(run.QueueName);

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportWithClientFromContainer(_ => client).AutoProvision();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema);

                var listener = opts.ListenToSqsQueue(run.QueueName, queue =>
                    {
                        // A message whose delete was lost comes back quickly
                        queue.VisibilityTimeout = 2;
                    })
                    .UseDurableInbox()

                    // One at a time, so a sentinel received after the redelivery is handled after it
                    .Sequential();

                if (shape == MessageShape.RawJson)
                {
                    listener.ReceiveRawJsonMessage(typeof(ForeignOrderPlaced));
                }
                else
                {
                    listener.ReceiveSnsTopicMessage(
                        new RawJsonSqsEnvelopeMapper(typeof(ForeignOrderPlaced), new JsonSerializerOptions()));
                }

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ForeignOrderPlacedHandler));
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        // The handled row has to still be retained when the message comes round again
        host.GetRuntime().DurabilitySettings.KeepAfterMessageHandling.ShouldBeGreaterThan(1.Minutes());

        var order = run.OrderNumber("order");

        if (redelivery == Redelivery.AfterTheDeleteWasLost)
        {
            var sqsMessageId = await run.SendAsync(order, snsMessageId: "sns-" + order);
            await ForeignOrderPlacedHandler.WaitForAsync(order);

            // The first delete went nowhere, so SQS makes the message visible again at the visibility timeout
            // and the listener receives it a second time, under the same SQS message id
            await client.WaitForRedeliveryToBeSettledAsync(sqsMessageId);
        }
        else
        {
            // Two SQS messages carrying one SNS notification
            client.LoseNoDeletes();

            await run.SendAsync(order, snsMessageId: "sns-" + order);
            await ForeignOrderPlacedHandler.WaitForAsync(order);

            var duplicate = await run.SendAsync(order, snsMessageId: "sns-" + order);
            await client.WaitForDeliveryToBeSettledAsync(duplicate);
        }

        // The listener is sequential, so once a sentinel received after the redelivery is handled, the
        // redelivered copy has either been handled already or was refused by the inbox
        var sentinel = run.OrderNumber("sentinel");
        await run.SendAsync(sentinel, snsMessageId: "sns-" + sentinel);
        await ForeignOrderPlacedHandler.WaitForAsync(sentinel);

        ForeignOrderPlacedHandler.TimesHandled(order).ShouldBe(1);
    }

    private class TestRun
    {
        private readonly string _id = Guid.NewGuid().ToString("N")[..8];
        private readonly MessageShape _shape;

        public TestRun(MessageShape shape)
        {
            _shape = shape;
            QueueName = $"foreign-redelivery-{_id}";
        }

        public string QueueName { get; }

        public string OrderNumber(string name) => $"{name}-{_id}";

        /// <summary>
        /// Send a message the way a producer that is not Wolverine does, and return its SQS message id
        /// </summary>
        public async Task<string> SendAsync(string orderNumber, string snsMessageId)
        {
            using var sqs = new AmazonSQSClient(new BasicAWSCredentials("ignore", "ignore"),
                new AmazonSQSConfig { ServiceURL = "http://localhost:4566" });

            var queueUrl = (await sqs.GetQueueUrlAsync(QueueName)).QueueUrl;

            var body = JsonSerializer.Serialize(new ForeignOrderPlaced(orderNumber));
            if (_shape == MessageShape.RawJsonInAnSnsNotification)
            {
                body = $$"""
                         {
                           "Type": "Notification",
                           "MessageId": "{{snsMessageId}}",
                           "TopicArn": "arn:aws:sns:us-east-1:000000000000:orders",
                           "Message": "{{HttpUtility.JavaScriptStringEncode(body)}}",
                           "Timestamp": "2026-10-28T06:42:22.618Z",
                           "UnsubscribeURL": "http"
                         }
                         """;
            }

            var response = await sqs.SendMessageAsync(queueUrl, body);
            return response.MessageId;
        }
    }

    /// <summary>
    /// The listener's SQS client, except that the first delete on the test's queue reports success without
    /// deleting anything. Also watches every receive and delete so the test knows when a delivery is settled.
    /// </summary>
    private class DeleteLosingSqsClient : AmazonSQSClient
    {
        private readonly string _queueName;
        private readonly ConcurrentDictionary<string, List<string>> _receiptHandles = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _deleted = new();
        private int _deletesToLose = 1;

        public DeleteLosingSqsClient(string queueName)
            : base(new BasicAWSCredentials("ignore", "ignore"), new AmazonSQSConfig { ServiceURL = "http://localhost:4566" })
        {
            _queueName = queueName;
        }

        public void LoseNoDeletes() => _deletesToLose = 0;

        public override async Task<ReceiveMessageResponse> ReceiveMessageAsync(ReceiveMessageRequest request,
            CancellationToken cancellationToken = default)
        {
            var response = await base.ReceiveMessageAsync(request, cancellationToken);

            if (isTheTestQueue(request.QueueUrl))
            {
                foreach (var message in response.Messages ?? [])
                {
                    var handles = _receiptHandles.GetOrAdd(message.MessageId, _ => []);
                    lock (handles) handles.Add(message.ReceiptHandle);
                }
            }

            return response;
        }

        public override async Task<DeleteMessageResponse> DeleteMessageAsync(DeleteMessageRequest request,
            CancellationToken cancellationToken = default)
        {
            if (isTheTestQueue(request.QueueUrl) && Interlocked.Decrement(ref _deletesToLose) >= 0)
            {
                return new DeleteMessageResponse { HttpStatusCode = System.Net.HttpStatusCode.OK };
            }

            var response = await base.DeleteMessageAsync(request, cancellationToken);
            deleted(request.ReceiptHandle);
            return response;
        }

        public override async Task<DeleteMessageBatchResponse> DeleteMessageBatchAsync(
            DeleteMessageBatchRequest request, CancellationToken cancellationToken = default)
        {
            if (isTheTestQueue(request.QueueUrl) && Interlocked.Decrement(ref _deletesToLose) >= 0)
            {
                return new DeleteMessageBatchResponse
                {
                    HttpStatusCode = System.Net.HttpStatusCode.OK,
                    Successful = request.Entries.Select(x => new DeleteMessageBatchResultEntry { Id = x.Id }).ToList(),
                    Failed = []
                };
            }

            var response = await base.DeleteMessageBatchAsync(request, cancellationToken);
            foreach (var entry in request.Entries)
            {
                if (response.Failed?.Any(x => x.Id == entry.Id) != true)
                {
                    deleted(entry.ReceiptHandle);
                }
            }

            return response;
        }

        /// <summary>
        /// Wait for the second delivery of this message to be deleted: stored in the inbox, or refused as a
        /// duplicate of the first, and settled either way
        /// </summary>
        public async Task WaitForRedeliveryToBeSettledAsync(string sqsMessageId)
        {
            var receiptHandle = await receiptHandleOfDeliveryAsync(sqsMessageId, 2);
            await deletedTask(receiptHandle).Task.WaitAsync(60.Seconds());
        }

        public async Task WaitForDeliveryToBeSettledAsync(string sqsMessageId)
        {
            var receiptHandle = await receiptHandleOfDeliveryAsync(sqsMessageId, 1);
            await deletedTask(receiptHandle).Task.WaitAsync(60.Seconds());
        }

        private async Task<string> receiptHandleOfDeliveryAsync(string sqsMessageId, int delivery)
        {
            for (var i = 0; i < 240; i++)
            {
                if (_receiptHandles.TryGetValue(sqsMessageId, out var handles))
                {
                    lock (handles)
                    {
                        if (handles.Count >= delivery) return handles[delivery - 1];
                    }
                }

                await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);
            }

            throw new TimeoutException($"SQS message {sqsMessageId} was not delivered {delivery} time(s)");
        }

        private void deleted(string receiptHandle) => deletedTask(receiptHandle).TrySetResult();

        private TaskCompletionSource deletedTask(string receiptHandle) =>
            _deleted.GetOrAdd(receiptHandle, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        private bool isTheTestQueue(string? queueUrl) => queueUrl != null && queueUrl.EndsWith("/" + _queueName);
    }
}
