using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Tracking;

public class EnvelopeRecordTests
{
    [Fact]
    public void creating_a_new_envelope_record_records_otel_activity()
    {
        using var source = new ActivitySource("Testing");

        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("Testing")
            .SetResourceBuilder(
                ResourceBuilder.CreateDefault()
                    .AddService("Wolverine", serviceVersion: "1.0"))
            .AddConsoleExporter()
            .Build();

        var root = source.CreateActivity("process", ActivityKind.Internal)!;
        root.Start();

        var parent = source.CreateActivity("process", ActivityKind.Internal)!;
        parent.Start();

        var child = source.CreateActivity("process", ActivityKind.Internal)!;
        child.Start();

        root.ShouldNotBeNull();
        parent.ShouldNotBeNull();

        child.ShouldNotBeNull();

        var record = new EnvelopeRecord(MessageEventType.Sent, ObjectMother.Envelope(), 1000, null);

        root.Id!.ShouldContain(record.RootId!);
        record.ParentId!.ShouldContain(parent.Id!);
        record.ActivityId.ShouldBe(child.Id);

        root.Stop();
        parent.Stop();
        child.Stop();
    }

    [Fact]
    public void to_string_for_auto_fault_published_event_uses_dedicated_format()
    {
        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            Message = new Fault<Foo>(
                Message: new Foo("a"),
                Exception: ExceptionInfo.From(new InvalidOperationException("boom")),
                Attempts: 1,
                FailedAt: DateTimeOffset.UtcNow,
                CorrelationId: null,
                ConversationId: Guid.NewGuid(),
                TenantId: null,
                Source: null,
                Headers: new Dictionary<string, string?>()),
        };

        var record = new EnvelopeRecord(MessageEventType.AutoFaultPublished, envelope, sessionTime: 100, exception: null)
        {
            ServiceName = "test-service",
            UniqueNodeId = Guid.NewGuid(),
        };

        record.ToString().ShouldContain("Auto-published Fault for");
    }

    private record Foo(string Name);
}
