using Microsoft.Extensions.DependencyInjection;
using TestingSupport.Compliance;
using Wolverine.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Compilation;

public class can_compile_a_handler_chain_for_an_inner_type : IntegrationContext
{
    private readonly ITestOutputHelper _output;

    public can_compile_a_handler_chain_for_an_inner_type(DefaultApp @default, ITestOutputHelper output) : base(@default)
    {
        _output = output;
    }

    [Fact]
    public void does_not_blow_up()
    {
        _output.WriteLine(Host.Services.GetRequiredService<IWolverineRuntime>().Options.DescribeHandlerMatch(typeof(ThingWithInner.InnerHandler)));

        var chain = Handlers.ChainFor<Message1>();
        var call = chain.Handlers.First(x => x.HandlerType == typeof(ThingWithInner.InnerHandler));
        call.ShouldNotBeNull();
    }
}

public class ThingWithInner
{
    public class InnerHandler
    {
        public void Handle(Message1 message)
        {
        }
    }
}