using System.Linq;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Lamar;
using TestMessages;
using Wolverine.Attributes;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Runtime.Handlers;

public class HandlerChainTester
{

    [Fact]
    public void create_by_method()
    {
        var chain = HandlerChain.For<Target>(x => x.Go(null), null);
        chain.MessageType.ShouldBe(typeof(Message1));

        var methodCall = chain.Handlers.Single();
        methodCall.HandlerType.ShouldBe(typeof(Target));
        methodCall.Method.Name.ShouldBe(nameof(Target.Go));
    }

    [Fact]
    public void create_by_static_method()
    {
        var chain = HandlerChain.For<Target>(nameof(Target.GoStatic), null);

        chain.MessageType.ShouldBe(typeof(Message2));

        var methodCall = chain.Handlers.Single();
        methodCall.HandlerType.ShouldBe(typeof(Target));
        methodCall.Method.Name.ShouldBe(nameof(Target.GoStatic));
    }

    [Fact]
    public void default_number_of_max_attempts_is_null()
    {
        var chain = HandlerChain.For<Target>(nameof(Target.GoStatic), null);
        chain.Failures.MaximumAttempts.HasValue.ShouldBeFalse();
    }

    public class Target
    {
        public void Go(Message1 message)
        {
        }

        public static void GoStatic(Message2 message)
        {
        }
    }


}