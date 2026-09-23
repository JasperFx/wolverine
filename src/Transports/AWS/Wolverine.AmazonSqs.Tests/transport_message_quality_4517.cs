using System.Globalization;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Xunit;

namespace Wolverine.AmazonSqs.Tests;

/// <summary>
/// GH-4517: every one of these messages used to be either empty or a bare sentence with nothing
/// in it a user could act on.
/// </summary>
public class transport_message_quality_4517
{
    [Fact]
    public void uri_with_the_wrong_scheme_names_the_expected_shape()
    {
        var transport = new AmazonSqsTransport();

        var ex = Should.Throw<ArgumentOutOfRangeException>(() =>
            transport.TryGetEndpoint(new Uri("sq://incoming")));

        ex.Message.ShouldContain("sqs://{queueName}");
        ex.Message.ShouldContain("sq://incoming");
    }

    [Fact]
    public void not_initialized_message_names_both_causes()
    {
        var message = AmazonSqsTransport.NotInitializedMessage(new Uri("sqs://incoming"));

        message.ShouldContain("sqs://incoming");
        message.ShouldContain("UseAmazonSqsTransport()");
        message.ShouldContain("has not been started");
    }

    [Fact]
    public async Task building_a_listener_before_the_transport_connects_explains_why()
    {
        var transport = new AmazonSqsTransport();
        var queue = transport.Queues["incoming"];

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await queue.BuildListenerAsync(null!, null!));

        ex.Message.ShouldContain("UseAmazonSqsTransport()");
        ex.Message.ShouldContain("sqs://incoming");
    }

    [Fact]
    public void bad_nservicebus_timestamp_names_the_value_and_the_expected_format()
    {
        var ex = Should.Throw<FormatException>(() => DateTimeOffsetHelper.ToDateTimeOffset("not a timestamp"));

        ex.Message.ShouldContain("not a timestamp");
        ex.Message.ShouldContain("yyyy-MM-dd HH:mm:ss:ffffff Z");
        ex.Message.ShouldContain("NServiceBus.TimeSent");
    }

    [Fact]
    public void a_well_formed_nservicebus_timestamp_still_round_trips()
    {
        var timestamp = new DateTimeOffset(2026, 9, 22, 13, 14, 15, TimeSpan.Zero);
        var wire = DateTimeOffsetHelper.ToWireFormattedString(timestamp);

        DateTimeOffsetHelper.ToDateTimeOffset(wire).ShouldBe(timestamp);
        wire.ShouldBe(timestamp.ToString("yyyy-MM-dd HH:mm:ss:ffffff Z", CultureInfo.InvariantCulture));
    }
}
