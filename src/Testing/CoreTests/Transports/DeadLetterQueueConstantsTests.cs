using Shouldly;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Transports;

public class DeadLetterQueueConstantsTests
{
    [Fact]
    public void stamp_failure_metadata_sets_all_headers()
    {
        var envelope = new Envelope();
        var exception = new InvalidOperationException("something went wrong");

        DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);

        envelope.Headers[DeadLetterQueueConstants.ExceptionTypeHeader]
            .ShouldBe(typeof(InvalidOperationException).FullName);
        envelope.Headers[DeadLetterQueueConstants.ExceptionMessageHeader]
            .ShouldBe("something went wrong");
        envelope.Headers[DeadLetterQueueConstants.ExceptionStackHeader]
            .ShouldNotBeNull();
        envelope.Headers[DeadLetterQueueConstants.FailedAtHeader]
            .ShouldNotBeNull();

        long.TryParse(envelope.Headers[DeadLetterQueueConstants.FailedAtHeader], out var timestamp)
            .ShouldBeTrue();
        timestamp.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void stamp_failure_metadata_preserves_existing_headers()
    {
        var envelope = new Envelope();
        envelope.Headers["custom-header"] = "custom-value";
        envelope.Headers["another"] = "one";

        var exception = new ArgumentException("bad arg");

        DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);

        envelope.Headers["custom-header"].ShouldBe("custom-value");
        envelope.Headers["another"].ShouldBe("one");
        envelope.Headers[DeadLetterQueueConstants.ExceptionTypeHeader]
            .ShouldBe(typeof(ArgumentException).FullName);
    }

    [Fact]
    public void stamp_failure_metadata_handles_null_stack_trace()
    {
        var envelope = new Envelope();
        // Exception created without throwing has null StackTrace
        var exception = new Exception("test");

        DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);

        envelope.Headers[DeadLetterQueueConstants.ExceptionStackHeader].ShouldBe("");
    }

    [Fact]
    public void stamp_failure_metadata_overwrites_previous_failure_headers()
    {
        var envelope = new Envelope();
        var firstException = new InvalidOperationException("first");
        DeadLetterQueueConstants.StampFailureMetadata(envelope, firstException);

        var secondException = new ArgumentException("second");
        DeadLetterQueueConstants.StampFailureMetadata(envelope, secondException);

        envelope.Headers[DeadLetterQueueConstants.ExceptionTypeHeader]
            .ShouldBe(typeof(ArgumentException).FullName);
        envelope.Headers[DeadLetterQueueConstants.ExceptionMessageHeader]
            .ShouldBe("second");
    }

    [Fact]
    public void stamp_failure_metadata_records_the_original_destination()
    {
        var envelope = new Envelope
        {
            Destination = new Uri("kafka://topic/incoming")
        };

        DeadLetterQueueConstants.StampFailureMetadata(envelope, new Exception("boom"));

        envelope.Headers[DeadLetterQueueConstants.OriginalDestinationHeader]
            .ShouldBe("kafka://topic/incoming");
    }

    [Fact]
    public void stamp_failure_metadata_omits_destination_and_partition_headers_when_unknown()
    {
        var envelope = new Envelope();

        DeadLetterQueueConstants.StampFailureMetadata(envelope, new Exception("boom"));

        envelope.Headers.ContainsKey(DeadLetterQueueConstants.OriginalDestinationHeader).ShouldBeFalse();
        envelope.Headers.ContainsKey(DeadLetterQueueConstants.OriginalPartitionHeader).ShouldBeFalse();
        envelope.Headers.ContainsKey(DeadLetterQueueConstants.OriginalOffsetHeader).ShouldBeFalse();
    }

    [Fact]
    public void stamp_failure_metadata_records_partition_and_offset_when_present()
    {
        var envelope = new Envelope
        {
            PartitionId = 3,
            Offset = 1234L
        };

        DeadLetterQueueConstants.StampFailureMetadata(envelope, new Exception("boom"));

        envelope.Headers[DeadLetterQueueConstants.OriginalPartitionHeader].ShouldBe("3");
        envelope.Headers[DeadLetterQueueConstants.OriginalOffsetHeader].ShouldBe("1234");
    }

    [Fact]
    public void stamp_failure_metadata_truncates_very_long_stack_traces()
    {
        var longStack = new string('x', DeadLetterQueueConstants.MaxStackTraceLength + 500);

        var truncated = DeadLetterQueueConstants.TruncateStackTrace(longStack);

        truncated.Length.ShouldBe(DeadLetterQueueConstants.MaxStackTraceLength
                                  + DeadLetterQueueConstants.TruncationMarker.Length);
        truncated.ShouldEndWith(DeadLetterQueueConstants.TruncationMarker);
    }

    [Fact]
    public void truncate_stack_trace_leaves_short_stacks_alone()
    {
        DeadLetterQueueConstants.TruncateStackTrace("short stack").ShouldBe("short stack");
        DeadLetterQueueConstants.TruncateStackTrace(null).ShouldBe("");
    }
}
