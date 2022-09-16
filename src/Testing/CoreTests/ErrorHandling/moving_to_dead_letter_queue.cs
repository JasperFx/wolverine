using System;
using System.Threading.Tasks;
using TestingSupport.ErrorHandling;
using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.ErrorHandling;

public class moving_to_dead_letter_queue : ErrorHandlingContext
{
    public moving_to_dead_letter_queue()
    {
        theOptions.Handlers.ConfigureHandlerForMessage<ErrorCausingMessage>(chain =>
        {
            chain.OnException<DivideByZeroException>().MoveToErrorQueue();
            chain.OnException<InvalidOperationException>().RetryTimes(3);

            chain.Failures.MaximumAttempts = 3;
        });
    }

    [Fact]
    public async Task move_to_dead_letter_queue_on_matching_exception()
    {
        throwOnAttempt<DataMisalignedException>(1);
        throwOnAttempt<DivideByZeroException>(2);

        await shouldMoveToErrorQueueOnAttempt(2);
    }

    [Fact] // Warning, this times out a little too easily. Retry it before you panic
    public async Task moves_to_dead_letter_queue_on_maximum_attempts()
    {
        throwOnAttempt<DataMisalignedException>(1);
        throwOnAttempt<DataMisalignedException>(2);
        throwOnAttempt<ArgumentNullException>(3);

        await shouldMoveToErrorQueueOnAttempt(3);
    }

    [Fact]
    public async Task send_with_no_errors()
    {
        await shouldSucceedOnAttempt(1);
    }

    [Fact]
    public async Task trips_off_on_first_filter()
    {
        throwOnAttempt<DivideByZeroException>(1);

        await shouldMoveToErrorQueueOnAttempt(1);
    }

    [Fact]
    public async Task still_moves_to_error_queue_on_unmatched_exception()
    {
        throwOnAttempt<ArgumentNullException>(1);
        throwOnAttempt<ArgumentNullException>(2);
        throwOnAttempt<ArgumentNullException>(3);

        await shouldMoveToErrorQueueOnAttempt(3);
    }
}
