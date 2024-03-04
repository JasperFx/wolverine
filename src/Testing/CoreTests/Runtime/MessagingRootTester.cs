using JasperFx.Core.Reflection;
using TestingSupport;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Runtime;

public class MessagingRootTester
{
    [Fact]
    public void create_bus_for_envelope()
    {
        var root = new MockWolverineRuntime();
        var original = ObjectMother.Envelope();

        var context1 = new MessageContext(root);
        context1.ReadEnvelope(original, InvocationCallback.Instance);
        var context = (IMessageContext)context1;

        context.Envelope.ShouldBe(original);
        context1.Transaction.ShouldNotBeNull();

        context.As<MessageContext>().Transaction.ShouldBeSameAs(context);
    }
}