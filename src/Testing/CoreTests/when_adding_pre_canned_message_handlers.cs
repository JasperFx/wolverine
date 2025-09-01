using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests;

public class when_adding_pre_canned_message_handlers
{
    public readonly WolverineOptions theOptions = new WolverineOptions();
    
    public record FakeMessage;

    public class FakeMessageHandler : MessageHandler
    {
        public override Task HandleAsync(MessageContext context, CancellationToken cancellation)
        {
            throw new NotImplementedException();
        }
    }

    public when_adding_pre_canned_message_handlers()
    {
        theOptions.AddMessageHandler(typeof(FakeMessage), new FakeMessageHandler());
    }

    [Fact]
    public void has_the_new_message_type()
    {
        theOptions.HandlerGraph.AllMessageTypes().ShouldContain(typeof(FakeMessage));
    }

    [Fact]
    public void can_handle_the_message_type()
    {
        theOptions.HandlerGraph.CanHandle(typeof(FakeMessage)).ShouldBeTrue();
    }
    
    [Fact]
    public void has_the_message_handler()
    {
        theOptions.HandlerGraph.HandlerFor(typeof(FakeMessage))
            .ShouldBeOfType<FakeMessageHandler>();
    }

    [Fact]
    public void because_it_is_a_message_handler_has_a_chain()
    {
        var handler = theOptions.HandlerGraph.HandlerFor<FakeMessage>()
            .ShouldBeOfType<FakeMessageHandler>();

        handler.Chain.ShouldNotBeNull();
    }
}