using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TestingSupport;
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

        var root = source.CreateActivity("process", ActivityKind.Internal);
        root.Start();

        var parent = source.CreateActivity("process", ActivityKind.Internal);
        parent.Start();

        var child = source.CreateActivity("process", ActivityKind.Internal);
        child.Start();

        root.ShouldNotBeNull();
        parent.ShouldNotBeNull();

        child.ShouldNotBeNull();

        var record = new EnvelopeRecord(MessageEventType.Sent, ObjectMother.Envelope(), 1000, null);

        root.Id.ShouldContain(record.RootId);
        record.ParentId.ShouldContain(parent.Id);
        record.ActivityId.ShouldBe(child.Id);

        root.Stop();
        parent.Stop();
        child.Stop();
    }
}