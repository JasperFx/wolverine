using System;
using System.Threading.Tasks;
using Xunit;

namespace CoreTests.ErrorHandling;

public class message_moves_to_error_queue_with_no_error_handling : ErrorHandlingContext
{
    [Fact]
    public async Task will_move_to_dead_letter_queue()
    {
        throwOnAttempt<InvalidOperationException>(1);
        throwOnAttempt<InvalidOperationException>(2);
        throwOnAttempt<InvalidOperationException>(3);

        await shouldMoveToErrorQueueOnAttempt(1);
    }
}
