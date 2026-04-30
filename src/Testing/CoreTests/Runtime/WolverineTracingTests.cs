using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Runtime;

public class WolverineTracingTests
{
    [Fact]
    public void use_parent_id_from_envelope_when_exists()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ParentId = "00-25d8f5709b569a1f61bcaf79b9450ed4-f293c0545fc237a1-01";

        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("Wolverine")
            .SetResourceBuilder(
                ResourceBuilder.CreateDefault()
                    .AddService("Wolverine", serviceVersion: "1.0"))
            .AddConsoleExporter()
            .Build();

        using var activity = WolverineTracing.StartEnvelopeActivity("process", envelope);
        activity.ShouldNotBeNull();
        activity.Start();

        activity.ParentId.ShouldBe(envelope.ParentId);
    }
}

public class when_creating_an_execution_activity
{
    private readonly Activity theActivity;
    private readonly Envelope theEnvelope;

    public when_creating_an_execution_activity()
    {
        theEnvelope = ObjectMother.Envelope();
        theEnvelope.ConversationId = Guid.NewGuid();

        theEnvelope.MessageType = "FooMessage";
        theEnvelope.CorrelationId = Guid.NewGuid().ToString();
        theEnvelope.Destination = new Uri("tcp://localhost:6666");
        theEnvelope.TenantId = "tenant3";

        theActivity = new Activity("process");
        theEnvelope.WriteTags(theActivity);
    }

    [Fact]
    public void does_not_set_saga_id_when_not_present()
    {
        theActivity.GetTagItem(WolverineTracing.SagaId).ShouldBeNull();
    }

    [Fact]
    public void should_set_the_tenant_id()
    {
        theActivity.GetTagItem(MetricsConstants.TenantIdKey).ShouldBe(theEnvelope.TenantId);
    }

    [Fact]
    public void should_set_the_otel_conversation_id_to_correlation_id()
    {
        theActivity.GetTagItem(WolverineTracing.MessagingConversationId)
            .ShouldBe(theEnvelope.CorrelationId);
    }

    [Fact]
    public void tags_the_message_id()
    {
        theActivity.GetTagItem(WolverineTracing.MessagingMessageId)
            .ShouldBe(theEnvelope.Id);
    }

    [Fact]
    public void sets_the_message_system_to_destination_uri_scheme()
    {
        theActivity.GetTagItem(WolverineTracing.MessagingSystem)
            .ShouldBe("tcp");
    }

    [Fact]
    public void sets_the_message_type_name()
    {
        theActivity.GetTagItem(WolverineTracing.MessageType)
            .ShouldBe(theEnvelope.MessageType);
    }

    [Fact]
    public void the_destination_should_be_the_envelope_destination()
    {
        theActivity.GetTagItem(WolverineTracing.MessagingDestination)
            .ShouldBe(theEnvelope.Destination);
    }

    [Fact]
    public void should_set_the_payload_size_bytes_when_it_exists()
    {
        theActivity.GetTagItem(WolverineTracing.PayloadSizeBytes)
            .ShouldBe(theEnvelope.Data!.Length);
    }

    [Fact]
    public void trace_the_conversation_id()
    {
        theActivity.GetTagItem(WolverineTracing.MessagingConversationId)
            .ShouldBe(theEnvelope.CorrelationId);
    }
}

public class when_saga_id_is_set_on_envelope
{
    private readonly Activity theActivity;
    private readonly Envelope theEnvelope;

    public when_saga_id_is_set_on_envelope()
    {
        theEnvelope = ObjectMother.Envelope();
        theEnvelope.SagaId = Guid.NewGuid().ToString();

        theActivity = new Activity("process");
        theEnvelope.WriteTags(theActivity);
    }

    [Fact]
    public void should_tag_activity_with_saga_id()
    {
        theActivity.GetTagItem(WolverineTracing.SagaId).ShouldBe(theEnvelope.SagaId);
    }
}

public class when_envelope_is_scheduled
{
    [Fact]
    public void tags_activity_when_scheduled_time_is_set()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);

        var activity = new Activity("process");
        envelope.WriteTags(activity);

        // Distinguishes a scheduled re-entrance (saga timeout firing,
        // deferred command, retry-with-delay) from an immediate dispatch
        // — see WolverineTracing.MessageScheduled.
        activity.GetTagItem(WolverineTracing.MessageScheduled).ShouldBe(true);
    }

    [Fact]
    public void does_not_tag_activity_when_scheduled_time_is_null()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ScheduledTime = null;

        var activity = new Activity("process");
        envelope.WriteTags(activity);

        activity.GetTagItem(WolverineTracing.MessageScheduled).ShouldBeNull();
    }
}