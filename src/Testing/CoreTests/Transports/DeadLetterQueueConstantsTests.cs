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
}
