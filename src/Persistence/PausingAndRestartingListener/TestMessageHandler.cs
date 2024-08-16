using JasperFx.Core;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace WolverineRepro;


public sealed record TestMessage();


public sealed class TestException : Exception { };


public sealed class TestMessageHandler
{
    public static void Configure(HandlerChain chain)
    {
        chain.OnException<TestException>()
            .Requeue().AndPauseProcessing(5.Seconds())
            .Then.MoveToErrorQueue();
    }

    public static void Handle(TestMessage message) => throw new TestException();
}
