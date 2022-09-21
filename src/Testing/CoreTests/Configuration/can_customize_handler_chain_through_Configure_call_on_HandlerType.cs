using CoreTests.Compilation;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Configuration;

public class can_customize_handler_chain_through_Configure_call_on_HandlerType : IntegrationContext
{
    public can_customize_handler_chain_through_Configure_call_on_HandlerType(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public void the_configure_method_is_found_and_used()
    {
        chainFor<SpecialMessage>().ShouldBeWrappedWith<CustomFrame>();
    }
}

public class BaseMessage{}

public class SpecialMessage : BaseMessage
{
}

#region sample_customized_handler_using_Configure

public class CustomizedHandler
{
    public void Handle(SpecialMessage message)
    {
        // actually handle the SpecialMessage
    }

    public static void Configure(HandlerChain chain)
    {
        chain.Middleware.Add(new CustomFrame());
    }
}

#endregion
