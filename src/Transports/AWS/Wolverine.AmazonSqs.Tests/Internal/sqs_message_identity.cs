using System.Text;
using System.Text.Json;
using System.Web;
using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Interop;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Tests.Internal;

public record ForeignOrderShipped(string OrderNumber);

/// <summary>
/// The durable inbox deduplicates on <see cref="Envelope.Id" />. A message from a producer that is not Wolverine
/// carries no Wolverine id, so the SQS listener derives one from the broker's identity of the message: every
/// delivery of the same message gets the same envelope id, and a redelivery is recognized as one.
///
/// <para>
/// These drive the listener's real receive path against a substitute SQS client: each "delivery" below is one
/// ReceiveMessage response handed to a freshly built listener, exactly as a redelivery after a lost delete or a
/// restarted node would arrive.
/// </para>
/// </summary>
public class sqs_message_identity
{
    private const string OrderBody = """{"OrderNumber":"ORD-1"}""";

    // ---- The broker's identity of the message

    [Fact]
    public async Task a_message_without_an_id_gets_the_same_id_on_every_delivery()
    {
        var first = await receiveAsync(rawJsonQueue("orders"), sqsMessage("sqs-message-1", OrderBody));
        var redelivery = await receiveAsync(rawJsonQueue("orders"), sqsMessage("sqs-message-1", OrderBody));

        first.Id.ShouldNotBe(Guid.Empty);
        redelivery.Id.ShouldBe(first.Id);
    }

    [Fact]
    public async Task a_different_message_gets_a_different_id()
    {
        var first = await receiveAsync(rawJsonQueue("orders"), sqsMessage("sqs-message-1", OrderBody));
        var second = await receiveAsync(rawJsonQueue("orders"), sqsMessage("sqs-message-2", OrderBody));

        second.Id.ShouldNotBe(first.Id);
    }

    [Fact]
    public async Task the_same_message_id_on_another_queue_gets_a_different_id()
    {
        var orders = await receiveAsync(rawJsonQueue("orders"), sqsMessage("sqs-message-1", OrderBody));
        var invoices = await receiveAsync(rawJsonQueue("invoices"), sqsMessage("sqs-message-1", OrderBody));

        invoices.Id.ShouldNotBe(orders.Id);
    }

    [Fact]
    public async Task the_same_queue_on_a_tenants_own_account_gets_a_different_id()
    {
        var defaultAccount = await receiveAsync(rawJsonQueue("orders"), sqsMessage("sqs-message-1", OrderBody));
        var tenantAccount = await receiveAsync(rawJsonQueue("orders"), sqsMessage("sqs-message-1", OrderBody),
            tenantId: "tenant-a");

        tenantAccount.Id.ShouldNotBe(defaultAccount.Id);
    }

    [Fact]
    public async Task the_same_queue_on_a_named_broker_gets_a_different_id()
    {
        var defaultBroker = await receiveAsync(rawJsonQueue("orders"), sqsMessage("sqs-message-1", OrderBody));
        var namedBroker = await receiveAsync(rawJsonQueue("orders", new AmazonSqsTransport("sqs-secondary")),
            sqsMessage("sqs-message-1", OrderBody));

        namedBroker.Id.ShouldNotBe(defaultBroker.Id);
    }

    // ---- An id the message carries, or a custom mapper assigns, always wins

    [Fact]
    public async Task a_wolverine_envelope_keeps_its_own_id()
    {
        var sent = new Envelope
        {
            Data = Encoding.UTF8.GetBytes(OrderBody),
            MessageType = "ForeignOrderShipped",
            ContentType = EnvelopeConstants.JsonContentType
        };
        var body = new DefaultSqsEnvelopeMapper().BuildMessageBody(sent);

        var received = await receiveAsync(queue("orders", new DefaultSqsEnvelopeMapper()),
            sqsMessage("sqs-message-1", body));

        received.Id.ShouldBe(sent.Id);
    }

    [Fact]
    public async Task an_id_assigned_by_a_custom_mapper_wins()
    {
        var assigned = Guid.NewGuid();

        var received = await receiveAsync(queue("orders", new CustomMapper(_ => assigned)),
            sqsMessage("sqs-message-1", OrderBody));

        received.Id.ShouldBe(assigned);
    }

    [Fact]
    public async Task a_custom_mapper_that_assigns_no_id_gets_the_derived_one()
    {
        var first = await receiveAsync(queue("orders", new CustomMapper(_ => null)),
            sqsMessage("sqs-message-1", OrderBody));
        var redelivery = await receiveAsync(queue("orders", new CustomMapper(_ => null)),
            sqsMessage("sqs-message-1", OrderBody));

        redelivery.Id.ShouldBe(first.Id);
    }

    [Fact]
    public async Task a_custom_mapper_that_could_not_read_an_id_gets_the_derived_one()
    {
        // Wolverine's own header mappers turn a missing or unparseable id into Guid.Empty, which the durable
        // receiver would otherwise replace with a brand new id just before the inbox insert
        var first = await receiveAsync(queue("orders", new CustomMapper(_ => Guid.Empty)),
            sqsMessage("sqs-message-1", OrderBody));
        var redelivery = await receiveAsync(queue("orders", new CustomMapper(_ => Guid.Empty)),
            sqsMessage("sqs-message-1", OrderBody));

        first.Id.ShouldNotBe(Guid.Empty);
        redelivery.Id.ShouldBe(first.Id);
    }

    // ---- CloudEvents ids are strings

    [Fact]
    public async Task a_cloud_event_with_a_uuid_id_keeps_it()
    {
        var eventId = Guid.NewGuid();

        var received = await receiveAsync(cloudEventsQueue("orders"),
            sqsMessage("sqs-message-1", cloudEvent(eventId.ToString())));

        received.Id.ShouldBe(eventId);
    }

    [Fact]
    public async Task a_cloud_event_whose_id_is_not_a_uuid_gets_the_same_id_on_every_delivery()
    {
        var first = await receiveAsync(cloudEventsQueue("orders"),
            sqsMessage("sqs-message-1", cloudEvent("order-1-shipped")));
        var redelivery = await receiveAsync(cloudEventsQueue("orders"),
            sqsMessage("sqs-message-1", cloudEvent("order-1-shipped")));

        redelivery.Id.ShouldBe(first.Id);
        redelivery.Message.ShouldBe(new ForeignOrderShipped("ORD-1"));
    }

    // ---- SNS delivers at least once too

    [Fact]
    public async Task an_sns_notification_delivered_twice_under_two_sqs_message_ids_gets_one_id()
    {
        var notification = snsNotification("sns-message-1", OrderBody);

        var first = await receiveAsync(snsQueue("orders"), sqsMessage("sqs-message-1", notification));
        var duplicate = await receiveAsync(snsQueue("orders"), sqsMessage("sqs-message-2", notification));

        duplicate.Id.ShouldBe(first.Id);
    }

    [Fact]
    public async Task an_sns_notification_fanned_out_to_two_queues_gets_a_different_id_on_each()
    {
        // Under the default MessageIdentity.IdOnly one id across both queues would make the shared inbox refuse
        // the second queue's copy as a duplicate of the first
        var notification = snsNotification("sns-message-1", OrderBody);

        var orders = await receiveAsync(snsQueue("orders"), sqsMessage("sqs-message-1", notification));
        var invoices = await receiveAsync(snsQueue("invoices"), sqsMessage("sqs-message-2", notification));

        invoices.Id.ShouldNotBe(orders.Id);
    }

    [Fact]
    public async Task a_cloud_event_in_an_sns_notification_is_keyed_on_the_notification()
    {
        var notification = snsNotification("sns-message-1", cloudEvent("order-1-shipped"));

        var first = await receiveAsync(cloudEventsQueue("orders"), sqsMessage("sqs-message-1", notification));
        var duplicate = await receiveAsync(cloudEventsQueue("orders"), sqsMessage("sqs-message-2", notification));

        duplicate.Id.ShouldBe(first.Id);
        duplicate.Message.ShouldBe(new ForeignOrderShipped("ORD-1"));
    }

    [Fact]
    public async Task a_raw_message_with_its_own_message_id_property_is_not_mistaken_for_a_notification()
    {
        const string body = """{"MessageId":"domain-id","OrderNumber":"ORD-1"}""";

        var first = await receiveAsync(snsQueue("orders"), sqsMessage("sqs-message-1", body));
        var another = await receiveAsync(snsQueue("orders"), sqsMessage("sqs-message-2", body));

        another.Id.ShouldNotBe(first.Id);
    }

    // ---- A message the sender split into fragments (GH-3926)

    [Fact]
    public async Task a_fragmented_message_gets_the_same_id_on_every_delivery_of_the_set()
    {
        var fragmentId = Guid.NewGuid();

        var first = await receiveAsync(rawJsonQueue("orders"),
            fragment(fragmentId, 0, "sqs-message-1", OrderBody[..10]),
            fragment(fragmentId, 1, "sqs-message-2", OrderBody[10..]));

        // SQS redelivers the fragments in whatever order it pleases
        var redelivery = await receiveAsync(rawJsonQueue("orders"),
            fragment(fragmentId, 1, "sqs-message-2", OrderBody[10..]),
            fragment(fragmentId, 0, "sqs-message-1", OrderBody[..10]));

        first.Message.ShouldBe(new ForeignOrderShipped("ORD-1"));
        redelivery.Id.ShouldBe(first.Id);
    }

    // ---- The name itself

    [Fact]
    public void the_components_of_the_name_are_delimited()
    {
        new SqsMessageIdentity("sqs", null, "ab").IdFor(SqsMessageIdentity.SqsMessageKind, "c")
            .ShouldNotBe(new SqsMessageIdentity("sqs", null, "a").IdFor(SqsMessageIdentity.SqsMessageKind, "bc"));

        new SqsMessageIdentity("sqs", null, "orders").IdFor(SqsMessageIdentity.SqsMessageKind, "id-1")
            .ShouldNotBe(new SqsMessageIdentity("sqs", null, "orders").IdFor(SqsMessageIdentity.SnsMessageKind, "id-1"));
    }

    [Fact]
    public void the_id_is_a_version_5_uuid()
    {
        var id = new SqsMessageIdentity("sqs", null, "orders").IdFor(SqsMessageIdentity.SqsMessageKind, "id-1");

        id.Version.ShouldBe(5);
        (id.Variant & 0xC).ShouldBe(0x8);
    }

    // ---- Harness

    private record TestQueue(AmazonSqsTransport Transport, AmazonSqsQueue Queue);

    private static TestQueue queue(string name, ISqsEnvelopeMapper mapper, AmazonSqsTransport? transport = null)
    {
        transport ??= new AmazonSqsTransport();
        var queue = transport.Queues[name];
        queue.Mapper = mapper;
        return new TestQueue(transport, queue);
    }

    private static TestQueue rawJsonQueue(string name, AmazonSqsTransport? transport = null)
    {
        return queue(name, new RawJsonSqsEnvelopeMapper(typeof(ForeignOrderShipped), new JsonSerializerOptions()),
            transport);
    }

    private static TestQueue snsQueue(string name)
    {
        return queue(name, new SnsTopicEnvelopeMapper(
            new RawJsonSqsEnvelopeMapper(typeof(ForeignOrderShipped), new JsonSerializerOptions())));
    }

    private static TestQueue cloudEventsQueue(string name)
    {
        var handlers = new HandlerGraph();
        handlers.RegisterMessageType(typeof(ForeignOrderShipped), "com.example.order.shipped");

        return queue(name, new CloudEventsSqsMapper(new CloudEventsMapper(handlers, new JsonSerializerOptions())));
    }

    private static Message sqsMessage(string messageId, string body)
    {
        return new Message
        {
            MessageId = messageId,
            ReceiptHandle = messageId + "-receipt-" + Guid.NewGuid(),
            Body = body
        };
    }

    private static Message fragment(Guid fragmentId, int index, string messageId, string body)
    {
        var message = sqsMessage(messageId, body);
        message.MessageAttributes = SqsMessageFragments.AttributesFor(fragmentId, index, 2);
        return message;
    }

    private static string cloudEvent(string id)
    {
        return $$"""
                 {
                   "specversion": "1.0",
                   "id": "{{id}}",
                   "source": "https://example.com/orders",
                   "type": "com.example.order.shipped",
                   "datacontenttype": "application/json",
                   "data": {{OrderBody}}
                 }
                 """;
    }

    private static string snsNotification(string snsMessageId, string message)
    {
        return $$"""
                 {
                   "Type": "Notification",
                   "MessageId": "{{snsMessageId}}",
                   "TopicArn": "arn:aws:sns:us-east-1:000000000000:orders",
                   "Message": "{{HttpUtility.JavaScriptStringEncode(message)}}",
                   "Timestamp": "2026-10-28T06:42:22.618Z",
                   "UnsubscribeURL": "http"
                 }
                 """;
    }

    /// <summary>
    /// Build a listener for the queue, let it receive the messages in one
    /// ReceiveMessage response, and return the single envelope it handed to the receiver.
    /// </summary>
    private static async Task<Envelope> receiveAsync(TestQueue queue, Message message, string? tenantId = null)
    {
        return await receiveAsync(queue, [message], tenantId);
    }

    private static Task<Envelope> receiveAsync(TestQueue queue, Message first, Message second)
    {
        return receiveAsync(queue, [first, second], null);
    }

    private static async Task<Envelope> receiveAsync(TestQueue testQueue, Message[] messages, string? tenantId)
    {
        var (transport, queue) = testQueue;

        var client = Substitute.For<IAmazonSQS>();
        client.CreateQueueAsync(Arg.Any<CreateQueueRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CreateQueueResponse { QueueUrl = "http://localhost:4566/000000000000/" + queue.QueueName });

        var delivered = false;
        client.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (delivered) return new ReceiveMessageResponse { Messages = [] };

                delivered = true;
                return new ReceiveMessageResponse { Messages = messages.ToList() };
            });

        transport.Client = client;
        await queue.SetupAsync(client);

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.DurabilitySettings.Returns(new DurabilitySettings());

        var received = new TaskCompletionSource<Envelope[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = Substitute.For<IReceiver>();
        receiver.ReceivedAsync(Arg.Any<IListener>(), Arg.Any<Envelope[]>())
            .Returns(call =>
            {
                received.TrySetResult(call.Arg<Envelope[]>());
                return ValueTask.CompletedTask;
            });

        await using var listener = new SqsListener(runtime, queue, transport, receiver, tenantId: tenantId);

        var envelopes = await received.Task.WaitAsync(10.Seconds());
        return envelopes.ShouldHaveSingleItem();
    }

    private class CustomMapper : ISqsEnvelopeMapper
    {
        private readonly Func<string, Guid?> _id;

        public CustomMapper(Func<string, Guid?> id)
        {
            _id = id;
        }

        public string BuildMessageBody(Envelope envelope) => throw new NotSupportedException();

        public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope) => [];

        public void ReadEnvelopeData(Envelope envelope, string messageBody,
            IDictionary<string, MessageAttributeValue> attributes)
        {
            envelope.Data = Encoding.UTF8.GetBytes(messageBody);
            envelope.MessageType = "ForeignOrderShipped";

            if (_id(messageBody) is { } id)
            {
                envelope.Id = id;
            }
        }
    }
}
