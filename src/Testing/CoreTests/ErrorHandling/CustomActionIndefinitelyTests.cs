using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.ErrorHandling;

public class CustomActionIndefinitelyTests
{
    private readonly Envelope _theEnvelope = ObjectMother.Envelope();
    private readonly HandlerGraph _theHandlers = new();

    [Fact]
    public void custom_action_only_runs_twice_with_current_CustomAction_implementation()
    {
        _theHandlers.OnException<SpecialException>()
            .CustomAction((runtime, lifecycle, ex) => ValueTask.CompletedTask,
                "Handle SpecialException with conditional discard/requeue");

        var exception = new SpecialException();

        // First attempt - should trigger custom action
        _theEnvelope.Attempts = 1;
        var continuation1 = _theHandlers.Failures.DetermineExecutionContinuation(exception, _theEnvelope);
        continuation1.ShouldBeOfType<LambdaContinuation>();

        // Second attempt - should default to MoveToErrorQueue because no InfiniteSource is set  
        _theEnvelope.Attempts = 2;
        var continuation2 = _theHandlers.Failures.DetermineExecutionContinuation(exception, _theEnvelope);
        continuation2.ShouldBeOfType<MoveToErrorQueue>();

        // Third attempt - should also default to MoveToErrorQueue
        _theEnvelope.Attempts = 3;
        var continuation3 = _theHandlers.Failures.DetermineExecutionContinuation(exception, _theEnvelope);
        continuation3.ShouldBeOfType<MoveToErrorQueue>();
    }

    [Fact]
    public void custom_action_indefinitely_should_run_until_user_decides_to_stop()
    {
        var callCount = 0;

        _theHandlers.OnException<SpecialException>()
            .CustomActionIndefinitely(async (runtime, lifecycle, ex) =>
            {
                callCount++;

                await Task.CompletedTask;
            }, "Handle SpecialException with conditional discard/requeue indefinitely");

        var exception = new SpecialException();

        // Test multiple attempts - should all trigger custom action
        for (int attempt = 1; attempt <= 15; attempt++)
        {
            _theEnvelope.Attempts = attempt;
            var continuation = _theHandlers.Failures.DetermineExecutionContinuation(exception, _theEnvelope);
            continuation.ShouldBeOfType<LambdaContinuation>();
        }
    }
}

public class SpecialException : Exception
{
    public int Code { get; set; }
}