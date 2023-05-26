using Shouldly;
using Wolverine.Tracking;

namespace TestingSupport.ErrorHandling;

public static class EnvelopeRecordExtensions
{
    public static void ShouldHaveSucceededOnAttempt(this EnvelopeRecord record, int attempt)
    {
        record.MessageEventType.ShouldBe(MessageEventType.MessageSucceeded);
        record.AttemptNumber.ShouldBe(3);
    }

    public static void ShouldHaveMovedToTheErrorQueueOnAttempt(this EnvelopeRecord record, int attempt)
    {
        record.MessageEventType.ShouldBe(MessageEventType.MovedToErrorQueue);
        record.AttemptNumber.ShouldBe(3);
    }
}