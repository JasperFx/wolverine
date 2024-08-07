using System.Diagnostics;
using CoreTests.Runtime;
using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.ErrorHandling;

public class RetryNowContinuationTester
{
    [Fact]
    public async Task just_calls_through_to_the_context_pipeline_to_do_it_again()
    {
        var continuation = RetryInlineContinuation.Instance;

        var envelope = ObjectMother.Envelope();
        envelope.Attempts = 1;

        var context = Substitute.For<IEnvelopeLifecycle>();
        context.Envelope.Returns(envelope);

        await continuation.ExecuteAsync(context, new MockWolverineRuntime(), DateTimeOffset.Now, new Activity("process"));

        await context.Received(1).RetryExecutionNowAsync();
    }
}