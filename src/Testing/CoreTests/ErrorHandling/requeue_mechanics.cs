using JasperFx.Core;
using TestingSupport.ErrorHandling;
using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.ErrorHandling;

public class requeue_mechanics : ErrorHandlingContext
{
    public requeue_mechanics()
    {
        ConfigureOptions(opts =>
        {
            opts.HandlerGraph.ConfigureHandlerForMessage<ErrorCausingMessage>(chain =>
            {
                chain.OnException<DivideByZeroException>()
                    .PauseThenRequeue(25.Milliseconds())
                    .Then.PauseThenRequeue(25.Milliseconds());
            });
        });
    }

    [Fact]
    public async Task can_requeue_and_finish()
    {
        throwOnAttempt<DivideByZeroException>(1);

        await shouldSucceedOnAttempt(2);
    }

    [Fact]
    public async Task can_requeue_and_finish_2()
    {
        throwOnAttempt<DivideByZeroException>(1);
        throwOnAttempt<DivideByZeroException>(2);

        var record = await afterProcessingIsComplete();

        record.ShouldHaveSucceededOnAttempt(3);
    }

    [Fact]
    public async Task moves_to_dead_letter_queueu_on_too_many_failures()
    {
        throwOnAttempt<DivideByZeroException>(1);
        throwOnAttempt<DivideByZeroException>(2);
        throwOnAttempt<DivideByZeroException>(3);

        var record = await afterProcessingIsComplete();

        record.ShouldHaveMovedToTheErrorQueueOnAttempt(3);
    }
}