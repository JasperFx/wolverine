using System;
using System.Threading.Tasks;
using TestingSupport.ErrorHandling;
using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.ErrorHandling;

public class Fallback_from_Chain_to_Global_Error_Handling : ErrorHandlingContext
{
    public Fallback_from_Chain_to_Global_Error_Handling()
    {
        theOptions.Policies.OnException<DivideByZeroException>().RetryTimes(3);
        theOptions.Policies.OnException<DataMisalignedException>().Requeue();
        theOptions.Policies.OnException<DataMisalignedException>().MoveToErrorQueue();

        theOptions.HandlerGraph.ConfigureHandlerForMessage<ErrorCausingMessage>(chain =>
        {
            chain.OnException<DivideByZeroException>().MoveToErrorQueue();
            chain.OnException<InvalidOperationException>().RetryTimes(3);
            chain.Failures.MaximumAttempts = 3;
        });
    }

    [Fact]
    public async Task chain_specific_rules_do_not_apply()
    {
        throwOnAttempt<DataMisalignedException>(1);
        throwOnAttempt<DataMisalignedException>(2);

        var record = await afterProcessingIsComplete();

        record.ShouldHaveSucceededOnAttempt(3);
    }

    [Fact] // this blinks some times with timing issues. Grrr.
    public async Task chain_specific_rules_catch()
    {
        throwOnAttempt<DivideByZeroException>(1);

        await shouldMoveToErrorQueueOnAttempt(1);
    }

    [Fact]
    public async Task chain_specific_rules_win_again()
    {
        throwOnAttempt<InvalidOperationException>(1);

        await shouldSucceedOnAttempt(2);
    }
}