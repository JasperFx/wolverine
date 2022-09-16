using System;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.ErrorHandling;

public class CircuitBreakerWrappedMessageHandlerTests
{
    private readonly CircuitBreakerWrappedMessageHandler theHandler;
    private readonly IMessageHandler theInnerHandler = Substitute.For<IMessageHandler>();

    private readonly IMessageSuccessTracker theTracker = Substitute.For<IMessageSuccessTracker>();

    public CircuitBreakerWrappedMessageHandlerTests()
    {
        theHandler = new CircuitBreakerWrappedMessageHandler(theInnerHandler, theTracker);
    }

    [Fact]
    public async Task successful_execution()
    {
        var context = Substitute.For<IMessageContext>();
        var token = CancellationToken.None;

        await theHandler.HandleAsync(context, token);

        await theInnerHandler.Received().HandleAsync(context, token);
        await theTracker.Received<IMessageSuccessTracker>().TagSuccessAsync();
    }

    [Fact]
    public async Task failed_execution()
    {
        var context = Substitute.For<IMessageContext>();
        var token = CancellationToken.None;

        var ex = new InvalidOperationException();

        theInnerHandler.HandleAsync(context, token).Throws(ex);

        Should.Throw<InvalidOperationException>(async () => { await theHandler.HandleAsync(context, token); });

        await theTracker.Received().TagFailureAsync(ex);
    }
}
