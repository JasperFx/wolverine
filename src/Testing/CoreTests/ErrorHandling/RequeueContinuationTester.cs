using System;
using System.Threading.Tasks;
using CoreTests.Messaging;
using CoreTests.Runtime;
using NSubstitute;
using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.ErrorHandling;

public class RequeueContinuationTester
{
    [Fact]
    public async Task executing_just_puts_it_back_in_line_at_the_back_of_the_queue()
    {
        var envelope = ObjectMother.Envelope();

        var context = Substitute.For<IMessageContext>();
        context.Envelope.Returns(envelope);


        await RequeueContinuation.Instance.ExecuteAsync(context, new MockWolverineRuntime(), DateTime.Now);

        await context.Received(1).DeferAsync();
    }
}
