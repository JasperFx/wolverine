using CoreTests.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Acceptance;

public class endpoint_specific_customizations : SendingContext
{
    [Fact]
    public void apply_customizations_to_certain_message_types_for_specific_type()
    {
        using var host = WolverineHost.For(opts =>
        {
            opts.PublishMessage<SpecialMessage>().To("stub://one")
                .CustomizeOutgoing(e => e.Headers.Add("a", "one"))
                .CustomizeOutgoingMessagesOfType<BaseMessage>(e => e.Headers.Add("d", "four"));

            opts.PublishMessage<OtherMessage>().To("stub://two")
                .CustomizeOutgoing(e => e.Headers.Add("b", "two"))
                .CustomizeOutgoing(e => e.Headers.Add("c", "three"))
                .CustomizeOutgoingMessagesOfType<OtherMessage>(e => e.Headers.Add("e", "five"));

            opts.ListenForMessagesFrom("stub://5678");
            opts.ListenForMessagesFrom("stub://6789");
        });

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var env1 = runtime.RoutingFor(typeof(SpecialMessage))
            .RouteForSend(new SpecialMessage(), null).Single();

        env1.Headers["a"].ShouldBe("one");
        env1.Headers.ContainsKey("b").ShouldBeFalse();
        env1.Headers.ContainsKey("c").ShouldBeFalse();

        var env2 = runtime.RoutingFor(typeof(OtherMessage))
            .RouteForSend(new OtherMessage(), null).Single();

        env2.Headers.ContainsKey("a").ShouldBeFalse();
        env2.Headers["b"].ShouldBe("two");
        env2.Headers["c"].ShouldBe("three");
    }

    public class CustomMessage;

    public class DifferentMessage;

    public class CustomMessageHandler
    {
        public void Handle(CustomMessage message)
        {
        }

        public void Handle(DifferentMessage message)
        {
        }
    }
}