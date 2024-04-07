using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Xunit;

namespace CoreTests.Configuration;

public class when_configuring_StrongOrderedGuarantees_on_listener_endpoint
{
    private TestEndpoint theEndpoint = new TestEndpoint(EndpointRole.Application)
    {
        SupportsInlineListeners = true,
        ListenerCount = 5
    };
    
    public when_configuring_StrongOrderedGuarantees_on_listener_endpoint()
    {
        var configuration = new ListenerConfiguration(theEndpoint);
        configuration.ListenWithStrictOrdering();
        
        configuration.As<IDelayedEndpointConfiguration>().Apply();
    }

    [Fact]
    public void should_be_a_listener()
    {
        theEndpoint.IsListener.ShouldBeTrue();
    }

    [Fact]
    public void listening_scope_is_exclusive()
    {
        theEndpoint.ListenerScope.ShouldBe(ListenerScope.Exclusive);
    }

    [Fact]
    public void execution_options_should_be_sequential()
    {
        theEndpoint.ExecutionOptions.SingleProducerConstrained.ShouldBeTrue();
        theEndpoint.ExecutionOptions.MaxDegreeOfParallelism.ShouldBe(1);
        theEndpoint.ExecutionOptions.EnsureOrdered.ShouldBeTrue();
    }

    [Fact]
    public void only_one_listener()
    {
        theEndpoint.ListenerCount.ShouldBe(1);
    }
    
    
}