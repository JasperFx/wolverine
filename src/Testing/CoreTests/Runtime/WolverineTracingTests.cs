using System.Diagnostics;
using CoreTests.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Tracing;
using Xunit;

namespace CoreTests.Runtime;

public class WolverineTracingTests
{
    [Fact]
    public void use_parent_id_from_envelope_when_exists()
    {
        WolverineActivitySource.Options = new();
        var envelope = ObjectMother.Envelope();
        envelope.ParentId = "00-25d8f5709b569a1f61bcaf79b9450ed4-f293c0545fc237a1-01";

        using var tracerProvider = BuildTracerProvider();

        using var activity = WolverineTracing.StartEnvelopeActivity("process", envelope, NullLogger.Instance);
        activity.ShouldNotBeNull();
        activity.Start();

        activity.ParentId.ShouldBe(envelope.ParentId);
    }

    [Fact]
    public void can_filter_process_activities()
    {
        WolverineActivitySource.Options = new();
        using var tracerProvider = BuildTracerProvider();
        const string messageTypeToFilter = "Wolverine.Runtime.Agents.CheckAgentHealth";
        var envelope = ObjectMother.Envelope();
        envelope.MessageType = messageTypeToFilter;
        WolverineActivitySource.Options.ExecuteEnvelopeFilter = env => env.MessageType != messageTypeToFilter;
        using var activity = WolverineTracing.StartExecuting(envelope, NullLogger.Instance);
        activity.ShouldBeNull();
    }

    [Fact]
    public void can_filter_send_activities()
    {
        WolverineActivitySource.Options = new();
        using var tracerProvider = BuildTracerProvider();
        const string messageTypeToFilter = "Wolverine.Runtime.Agents.TryAssumeLeadership";
        var envelope = ObjectMother.Envelope();
        envelope.MessageType = messageTypeToFilter;
        WolverineActivitySource.Options.SendEnvelopeFilter = env => env.MessageType != messageTypeToFilter;
        using var activity = WolverineTracing.StartSending(envelope, NullLogger.Instance);
        activity.ShouldBeNull();
    }
    [Fact]
    public void can_filter_out_internal_messages()
    {
        WolverineActivitySource.Options = new();
        using var tracerProvider = BuildTracerProvider();
        var envelope = ObjectMother.Envelope();
        envelope.Message = new CheckAgentHealth();
        WolverineActivitySource.Options.SuppressInternalMessageTypes = true;
        using var activity = WolverineTracing.StartReceiving(envelope, NullLogger.Instance);
        activity.ShouldBeNull();
    }

    [Fact]
    public void can_filter_receive_activities()
    {
        WolverineActivitySource.Options = new();
        using var tracerProvider = BuildTracerProvider();
        const string messageTypeToFilter = "Wolverine.Runtime.Agents.CheckAgentHealth";
        var envelope = ObjectMother.Envelope();
        envelope.MessageType = messageTypeToFilter;
        WolverineActivitySource.Options.ReceiveEnvelopeFilter = env => env.MessageType != messageTypeToFilter;
        using var activity = WolverineTracing.StartReceiving(envelope, NullLogger.Instance);
        activity.ShouldBeNull();
    }

    [Fact]
    public void can_filter_with_global_rule()
    {
        WolverineActivitySource.Options = new();
        using var tracerProvider = BuildTracerProvider();
        const string messageTypeToFilter = "Wolverine.Runtime.Agents.CheckAgentHealth";
        var envelope = ObjectMother.Envelope();
        envelope.MessageType = messageTypeToFilter;
        WolverineActivitySource.Options.GlobalFilter = env => env.MessageType != messageTypeToFilter;
        using var receiveActivity = WolverineTracing.StartReceiving(envelope, NullLogger.Instance);
        using var sendActivity = WolverineTracing.StartSending(envelope, NullLogger.Instance);
        using var executeActivity = WolverineTracing.StartExecuting(envelope, NullLogger.Instance);
        using var someOtherActivity =
            WolverineTracing.StartEnvelopeActivity("some other", envelope, NullLogger.Instance);
        new[] { receiveActivity, sendActivity, executeActivity, someOtherActivity }.ShouldAllBe(x => x == null);
    }

    [Fact]
    public void can_enrich_activities_by_configuration()
    {
        WolverineActivitySource.Options = new();
        using var tracerProvider = BuildTracerProvider();
        const string expectedTagName = "EnrichedTagForSend";
        const string expectedTagValue = "Enriched Send Tag Value";
        const string notExpectedTagName = "EnrichedTagForExecute";
        const string notExpectedTagValue = "Enriched Execute Tag Value";
        var envelope = ObjectMother.Envelope();
        envelope.Headers[expectedTagName] = expectedTagValue;
        envelope.Headers[notExpectedTagName] = notExpectedTagValue;
        WolverineActivitySource.Options.Enrich = (activity, eventType, env) =>
        {
            if (eventType == WolverineEnrichEventNames.StartSendEnvelope)
            {
                activity.SetTag(expectedTagName, env.Headers[expectedTagName]!);
            }

            if (eventType == WolverineEnrichEventNames.StartExecutingEnvelope)
            {
                activity.SetTag(notExpectedTagName, env.Headers[notExpectedTagName]!);
            }
        };
        using var activity = WolverineTracing.StartSending(envelope, NullLogger.Instance);
        activity.ShouldSatisfyAllConditions(
            x => x.GetTagItem(expectedTagName).ShouldBe(expectedTagValue),
            x => x.GetTagItem(notExpectedTagName).ShouldBeNull());
    }

    [Fact]
    public void can_handle_filtering_exceptions()
    {
        using var tracerProvider = BuildTracerProvider();
        var envelope = ObjectMother.Envelope();
        WolverineActivitySource.Options.GlobalFilter = _ => throw new Exception("The exception");
        var logger = Substitute.For<ILogger>();
        using var activity = WolverineTracing.StartSending(envelope, logger);
        activity.ShouldBeNull();
        logger.ReceivedWithAnyArgs().LogError(default(Exception), default);
    }

    [Fact]
    public void can_handle_enrichment_exceptions()
    {
        WolverineActivitySource.Options = new();
        using var tracerProvider = BuildTracerProvider();
        var envelope = ObjectMother.Envelope();
        WolverineActivitySource.Options.Enrich = (_, __, ___) => throw new Exception("the exception");
        var logger = Substitute.For<ILogger>();
        using var activity = WolverineTracing.StartSending(envelope, logger);
        activity.ShouldNotBeNull();
        logger.ReceivedWithAnyArgs().LogError(default(Exception), default);
    }
    
    private TracerProvider BuildTracerProvider()
    =>Sdk.CreateTracerProviderBuilder()
        .AddSource("Wolverine")
        .SetResourceBuilder(
            ResourceBuilder.CreateDefault()
                .AddService("Wolverine", serviceVersion: "1.0"))
        .AddConsoleExporter()
        .Build();
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
    public void should_set_the_tenant_id()
    {
        theActivity.GetTagItem(MetricsConstants.TenantIdKey).ShouldBe(theEnvelope.TenantId);
    }

    [Fact]
    public void should_set_the_otel_conversation_id_to_correlation_id()
    {
        theActivity.GetTagItem(WolverineTracing.MessagingConversationId)
            .ShouldBe(theEnvelope.ConversationId);
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
            .ShouldBe(theEnvelope.ConversationId);
    }
}