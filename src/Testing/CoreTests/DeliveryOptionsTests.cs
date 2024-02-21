using System;
using CoreTests.Messaging;
using CoreTests.Util;
using Wolverine.Util;
using Xunit;
using JasperFx.Core;

namespace CoreTests;

public class DeliveryOptionsTests
{
    [Fact]
    public void set_deliver_by_threshold()
    {
        var options = new DeliveryOptions
        {
            DeliverWithin = 5.Minutes()
        };

        options.DeliverBy.ShouldNotBeNull();

        options.DeliverBy.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(-5));
        options.DeliverBy.Value.ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(5));
    }

    [Fact]
    public void schedule_delay_prop_sets_deliver_by()
    {
        var options = new DeliveryOptions
        {
            ScheduleDelay = 5.Minutes()
        };

        var envelope = new Envelope();
        
        options.Override(envelope);
        
        
        envelope.ScheduleDelay.ShouldBe(5.Minutes());
        envelope.ScheduledTime.ShouldNotBeNull();

        envelope.ScheduledTime.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(-5));
        envelope.ScheduledTime.Value.ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(5));
    }

    [Fact]
    public void override_tenant_id()
    {
        var options = new DeliveryOptions
        {
            TenantId = "tenant4"
        };

        var envelope = new Envelope();
        
        options.Override(envelope);
        
        envelope.TenantId.ShouldBe("tenant4");
    }

    [Fact]
    public void override_group_id()
    {
        var options = new DeliveryOptions
        {
            GroupId = "group1"
        };

        var envelope = new Envelope();
        
        options.Override(envelope);
        
        envelope.GroupId.ShouldBe("group1");
    }

    [Fact]
    public void override_deduplication_id()
    {
        var options = new DeliveryOptions()
        {
            DeduplicationId = "foo"
        };
        
        var envelope = new Envelope();
        
        options.Override(envelope);
        
        envelope.DeduplicationId.ShouldBe("foo");
    }

    [Fact]
    public void override_ack_requested()
    {
        var options = new DeliveryOptions
        {
            AckRequested = true
        };

        var envelope = ObjectMother.Envelope();
        envelope.AckRequested = false;

        options.Override(envelope);

        envelope.AckRequested.ShouldBeTrue();
    }

    [Fact]
    public void do_not_override_ack_requested_if_not_explicit()
    {
        var options = new DeliveryOptions();

        var envelope = ObjectMother.Envelope();
        envelope.AckRequested = true;

        options.Override(envelope);

        envelope.AckRequested.ShouldBeTrue();
    }


    [Fact]
    public void do_not_override_scheduled_time_if_not_explicit()
    {
        var options = new DeliveryOptions();

        var envelope = ObjectMother.Envelope();
        envelope.ScheduledTime = new DateTimeOffset(DateTime.Today);

        options.Override(envelope);

        envelope.ScheduledTime.HasValue.ShouldBeTrue();
    }

    [Fact]
    public void override_scheduled_time()
    {
        var options = new DeliveryOptions
        {
            ScheduledTime = new DateTimeOffset(DateTime.Today)
        };

        var envelope = ObjectMother.Envelope();

        options.Override(envelope);

        envelope.ScheduledTime.Value.ShouldBe(options.ScheduledTime.Value);
    }

    [Fact]
    public void do_not_override_deliver_by_if_not_explicit()
    {
        var options = new DeliveryOptions();

        var envelope = ObjectMother.Envelope();
        envelope.DeliverBy = new DateTimeOffset(DateTime.Today);

        options.Override(envelope);

        envelope.DeliverBy.HasValue.ShouldBeTrue();
    }

    [Fact]
    public void override_delivery_by_time()
    {
        var options = new DeliveryOptions
        {
            DeliverBy = new DateTimeOffset(DateTime.Today)
        };

        var envelope = ObjectMother.Envelope();

        options.Override(envelope);

        envelope.DeliverBy.Value.ShouldBe(options.DeliverBy.Value);
    }

    [Fact]
    public void override_headers()
    {
        var options = new DeliveryOptions().WithHeader("a", "1").WithHeader("b", "2");
        var envelope = ObjectMother.Envelope();

        envelope.Headers["a"] = "5";

        options.Override(envelope);

        envelope.Headers["a"].ShouldBe("1");
        envelope.Headers["b"].ShouldBe("2");
    }

    [Fact]
    public void override_response_by_type()
    {
        var options = DeliveryOptions.RequireResponse<MySpecialMessage>();

        var envelope = ObjectMother.Envelope();

        options.Override(envelope);

        envelope.ReplyRequested.ShouldBe(typeof(MySpecialMessage).ToMessageTypeName());
    }

    [Fact]
    public void override_saga_id()
    {
        var options = new DeliveryOptions
        {
            SagaId = "foo"
        };

        var envelope = ObjectMother.Envelope();
        envelope.SagaId = "bar";

        options.Override(envelope);

        envelope.SagaId.ShouldBe("foo");
    }

    [Fact]
    public void do_not_override_saga_id_if_not_explicit()
    {
        var options = new DeliveryOptions();

        var envelope = ObjectMother.Envelope();
        envelope.SagaId = "bar";

        options.Override(envelope);

        envelope.SagaId.ShouldBe("bar");
    }

    [Fact]
    public void override_content_type()
    {
        var options = new DeliveryOptions
        {
            ContentType = "text/plain"
        };

        var envelope = ObjectMother.Envelope();
        envelope.ContentType = EnvelopeConstants.JsonContentType;

        options.Override(envelope);

        envelope.ContentType.ShouldBe("text/plain");
    }

    [Fact]
    public void do_not_override_content_type_if_not_explicit()
    {
        var options = new DeliveryOptions();

        var envelope = ObjectMother.Envelope();
        envelope.ContentType = EnvelopeConstants.JsonContentType;

        options.Override(envelope);

        envelope.ContentType.ShouldBe(EnvelopeConstants.JsonContentType);
    }

    [Fact]
    public void override_is_response()
    {
        var options = new DeliveryOptions { IsResponse = true };

        var envelope = ObjectMother.Envelope();
        envelope.IsResponse.ShouldBeFalse();

        options.Override(envelope);

        envelope.IsResponse.ShouldBeTrue();
    }
}