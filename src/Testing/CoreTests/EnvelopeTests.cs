using System;
using System.Threading.Tasks;
using CoreTests.Messaging;
using JasperFx.Core;
using NSubstitute;
using TestMessages;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Scheduled;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;
using Xunit;

namespace CoreTests;

public class EnvelopeTests
{
    [Fact]
    public void automatically_set_the_message_type_header_off_of_the_message()
    {
        var envelope = new Envelope
        {
            Message = new Message1(),
            Headers =
            {
                ["a"] = "1",
                ["b"] = "2"
            }
        };

        envelope.MessageType.ShouldBe(typeof(Message1).ToMessageTypeName());
    }

    [Fact]
    public void default_values_for_original_and_parent_id_are_null()
    {
        var parent = new Envelope();

        parent.CorrelationId.ShouldBeNull();
        parent.ConversationId.ShouldBe(Guid.Empty);
    }

    [Fact]
    public void envelope_for_ping()
    {
        var envelope = Envelope.ForPing(TransportConstants.LocalUri);
        envelope.MessageType.ShouldBe(Envelope.PingMessageType);
        envelope.Data.ShouldNotBeNull();
    }


    [Fact]
    public void execution_time_is_null_by_default()
    {
        new Envelope().ScheduledTime.ShouldBeNull();
    }


    [Fact]
    public void for_response_copies_the_saga_id_from_the_parent()
    {
        var parent = ObjectMother.Envelope();
        parent.SagaId = Guid.NewGuid().ToString();

        var response = parent.CreateForResponse(new Message2());
        response.SagaId.ShouldBe(parent.SagaId);
    }


    [Fact]
    public void has_a_correlation_id_by_default()
    {
        new Envelope().Id.ShouldNotBe(Guid.Empty);

        new Envelope().Id.ShouldNotBe(new Envelope().Id);
        new Envelope().Id.ShouldNotBe(new Envelope().Id);
        new Envelope().Id.ShouldNotBe(new Envelope().Id);
        new Envelope().Id.ShouldNotBe(new Envelope().Id);
        new Envelope().Id.ShouldNotBe(new Envelope().Id);
    }

    [Fact]
    public void if_reply_requested_header_exists_in_parent_and_matches_the_message_type()
    {
        var parent = new Envelope
        {
            CorrelationId = Guid.NewGuid().ToString(),
            ReplyUri = "foo://bar".ToUri(),
            ReplyRequested = typeof(Message1).ToMessageTypeName()
        };

        var childMessage = new Message1();

        var child = parent.CreateForResponse(childMessage);

        child.ConversationId.ShouldBe(parent.Id);
        child.Destination.ShouldBe(parent.ReplyUri);
    }

    [Fact]
    public void is_expired()
    {
        var envelope = new Envelope
        {
            DeliverBy = null
        };

        envelope.IsExpired().ShouldBeFalse();

        envelope.DeliverBy = DateTimeOffset.Now.AddSeconds(-1);
        envelope.IsExpired().ShouldBeTrue();

        envelope.DeliverBy = DateTimeOffset.Now.AddHours(1);

        envelope.IsExpired().ShouldBeFalse();
    }

    [Fact]
    public void original_message_creating_child_envelope()
    {
        var parent = new Envelope();

        var childMessage = new Message1();

        var child = parent.CreateForResponse(childMessage);

        child.Message.ShouldBeSameAs(childMessage);

        child.CorrelationId.ShouldBe(parent.CorrelationId);
        child.ConversationId.ShouldBe(parent.Id);
    }

    [Fact]
    public void parent_that_is_not_original_creating_child_envelope()
    {
        var parent = new Envelope
        {
            CorrelationId = Guid.NewGuid().ToString()
        };

        var childMessage = new Message1();

        var child = parent.CreateForResponse(childMessage);

        child.Message.ShouldBeSameAs(childMessage);

        child.CorrelationId.ShouldBe(parent.CorrelationId);
        child.ConversationId.ShouldBe(parent.Id);
    }


    [Fact]
    public void mark_received_when_not_delayed_execution()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ScheduledTime = null;

        var uri = TransportConstants.LocalUri;
        var settings = new NodeSettings(null);

        var listener = Substitute.For<IListener>();
        listener.Address.Returns(uri);
        envelope.MarkReceived(listener, DateTimeOffset.Now, settings);

        envelope.Status.ShouldBe(EnvelopeStatus.Incoming);
        envelope.OwnerId.ShouldBe(settings.UniqueNodeId);
    }

    [Fact]
    public void marked_received_sets_the_destination()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ScheduledTime = DateTimeOffset.Now.AddDays(-1);

        var uri = TransportConstants.LocalUri;
        var settings = new NodeSettings(null);

        var listener = Substitute.For<IListener>();
        listener.Address.Returns(uri);

        envelope.MarkReceived(listener, DateTimeOffset.Now, settings);

        envelope.Destination.ShouldBe(uri);
    }


    [Fact]
    public void mark_received_when_expired_execution()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ScheduledTime = DateTimeOffset.Now.AddDays(-1);

        var uri = TransportConstants.LocalUri;
        var listener = Substitute.For<IListener>();
        listener.Address.Returns(uri);

        var settings = new NodeSettings(null);

        envelope.MarkReceived(listener, DateTimeOffset.Now, settings);

        envelope.Status.ShouldBe(EnvelopeStatus.Incoming);
        envelope.OwnerId.ShouldBe(settings.UniqueNodeId);
    }

    [Fact]
    public void mark_received_when_it_has_a_later_execution_time()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ScheduledTime = DateTimeOffset.Now.AddDays(1);

        var uri = TransportConstants.LocalUri;
        var listener = Substitute.For<IListener>();
        listener.Address.Returns(uri);

        var settings = new NodeSettings(null);

        envelope.MarkReceived(listener, DateTimeOffset.Now, settings);

        envelope.Status.ShouldBe(EnvelopeStatus.Scheduled);
        envelope.OwnerId.ShouldBe(TransportConstants.AnyNode);
    }

    [Fact]
    public async Task should_persist_when_sender_is_durable_and_it_is_outgoing()
    {
        var transaction = Substitute.For<IEnvelopeTransaction>();
        var sender = Substitute.For<ISendingAgent>();

        sender.IsDurable.Returns(true);
        sender.Latched.Returns(false);

        var envelope = ObjectMother.Envelope();
        envelope.OwnerId = 33333;
        envelope.Sender = sender;
        envelope.Status = EnvelopeStatus.Outgoing;
        envelope.Destination = new Uri("tcp://localhost:100"); // just to make it be not remote

        await envelope.PersistAsync(transaction);

        await transaction.Received().PersistOutgoingAsync(envelope);
        envelope.OwnerId.ShouldBe(33333);
    }
    
    [Fact]
    public async Task should_persist_when_sender_is_durable_and_it_is_scheduled()
    {
        var transaction = Substitute.For<IEnvelopeTransaction>();
        var sender = Substitute.For<ISendingAgent>();

        sender.IsDurable.Returns(true);
        sender.Latched.Returns(false);

        var envelope = ObjectMother.Envelope();
        envelope.OwnerId = 33333;
        envelope.Sender = sender;
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.Destination = new Uri("tcp://localhost:100"); // just to make it be not remote

        await envelope.PersistAsync(transaction);

        await transaction.Received().PersistIncomingAsync(envelope);
        envelope.OwnerId.ShouldBe(33333);
    }

    [Fact]
    public async Task should_persist_when_sender_is_durable_but_set_owner_to_0_when_sender_is_latched()
    {
        var transaction = Substitute.For<IEnvelopeTransaction>();
        var sender = Substitute.For<ISendingAgent>();

        sender.IsDurable.Returns(true);
        sender.Latched.Returns(true);

        var envelope = ObjectMother.Envelope();
        envelope.OwnerId = 33333;
        envelope.Sender = sender;
        envelope.Destination = new Uri("tcp://localhost:100"); // just to make it be not remote

        await envelope.PersistAsync(transaction);
        

        await transaction.Received().PersistOutgoingAsync(envelope);
        envelope.OwnerId.ShouldBe(TransportConstants.AnyNode);
    }

    [Fact]
    public async Task do_not_persist_when_sender_is_not_durable()
    {
        var transaction = Substitute.For<IEnvelopeTransaction>();
        var sender = Substitute.For<ISendingAgent>();

        sender.IsDurable.Returns(false);
        sender.Latched.Returns(false);

        var envelope = ObjectMother.Envelope();
        envelope.OwnerId = 33333;
        envelope.Sender = sender;

        await envelope.PersistAsync(transaction);

        await transaction.DidNotReceive().PersistOutgoingAsync(envelope);
    }

    [Fact]
    public async Task quick_send_when_sender_is_not_latched()
    {
        var sender = Substitute.For<ISendingAgent>();

        sender.Latched.Returns(false);

        var envelope = ObjectMother.Envelope();
        envelope.OwnerId = 33333;
        envelope.Sender = sender;

        await envelope.QuickSendAsync();

        await sender.Received().EnqueueOutgoingAsync(envelope);
    }

    [Fact]
    public async Task quick_send_when_sender_is_latched_should_not_send()
    {
        var sender = Substitute.For<ISendingAgent>();

        sender.Latched.Returns(true);
        sender.IsDurable.Returns(true);

        var envelope = ObjectMother.Envelope();
        envelope.OwnerId = 33333;
        envelope.Sender = sender;

        await envelope.QuickSendAsync();

        await sender.DidNotReceive().EnqueueOutgoingAsync(envelope);
    }

    [Fact]
    public void prepare_for_persistence_when_not_scheduled()
    {
        var envelope = new Envelope
        {
            ScheduledTime = null
        };

        var settings = new NodeSettings(null);

        envelope.PrepareForIncomingPersistence(DateTimeOffset.UtcNow, settings);

        envelope.Status.ShouldBe(EnvelopeStatus.Incoming);
        envelope.OwnerId.ShouldBe(settings.UniqueNodeId);
    }

    [Fact]
    public void set_schedule_delay()
    {
        var envelope = new Envelope
        {
            ScheduleDelay = 1.Days()
        };
        
        envelope.ScheduledTime.Value.Date.ShouldBe(DateTime.Today.AddDays(1));
        envelope.ScheduleDelay.ShouldBe(1.Days());
    }

    [Fact]
    public void set_deliver_within()
    {
        var envelope = new Envelope
        {
            DeliverWithin = 1.Days()
        };
        
        envelope.DeliverBy.Value.Date.ShouldBe(DateTime.Today.AddDays(1));
        envelope.DeliverWithin.ShouldBe(1.Days());
    }
    
    [Fact]
    public void prepare_for_persistence_when_scheduled_in_the_future()
    {
        var envelope = new Envelope
        {
            ScheduleDelay = 1.Days()
        };

        var settings = new NodeSettings(null);

        envelope.PrepareForIncomingPersistence(DateTimeOffset.UtcNow, settings);

        envelope.Status.ShouldBe(EnvelopeStatus.Scheduled);
        envelope.OwnerId.ShouldBe(TransportConstants.AnyNode);
    }

    [Fact]
    public void prepare_for_persistence_when_scheduled_in_the_past()
    {
        var envelope = new Envelope
        {
            ScheduledTime = DateTimeOffset.UtcNow.AddDays(-1)
        };

        var settings = new NodeSettings(null);

        envelope.PrepareForIncomingPersistence(DateTimeOffset.UtcNow, settings);

        envelope.Status.ShouldBe(EnvelopeStatus.Incoming);
        envelope.OwnerId.ShouldBe(settings.UniqueNodeId);
    }

    public class when_building_an_envelope_for_scheduled_send
    {
        private readonly ISendingAgent theSubscriber;
        private readonly Envelope theOriginal;
        private readonly Envelope theScheduledEnvelope;

        public when_building_an_envelope_for_scheduled_send()
        {
            theOriginal = ObjectMother.Envelope();
            theOriginal.ScheduledTime = DateTimeOffset.Now.Date.AddDays(2);
            theOriginal.Destination = "tcp://server3:2345".ToUri();

            theSubscriber = Substitute.For<ISendingAgent>();

            theScheduledEnvelope = theOriginal.ForScheduledSend(theSubscriber);
        }

        [Fact]
        public void the_message_should_be_the_original_envelope()
        {
            theScheduledEnvelope.Message.ShouldBeSameAs(theOriginal);
        }

        [Fact]
        public void execution_time_is_copied()
        {
            theScheduledEnvelope.ScheduledTime.ShouldBe(theOriginal.ScheduledTime);
        }

        [Fact]
        public void destination_is_scheduled_queue()
        {
            theScheduledEnvelope.Destination.ShouldBe(TransportConstants.DurableLocalUri);
        }

        [Fact]
        public void status_should_be_scheduled()
        {
            theScheduledEnvelope.Status.ShouldBe(EnvelopeStatus.Scheduled);
        }

        [Fact]
        public void owner_should_be_any_node()
        {
            theScheduledEnvelope.OwnerId.ShouldBe(TransportConstants.AnyNode);
        }

        [Fact]
        public void the_message_type_is_envelope()
        {
            theScheduledEnvelope.MessageType.ShouldBe(TransportConstants.ScheduledEnvelope);
        }

        [Fact]
        public void the_content_type_should_be_binary_envelope()
        {
            theScheduledEnvelope.ContentType.ShouldBe(TransportConstants.SerializedEnvelope);
        }
    }
}