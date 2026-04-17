using CoreTests.Compilation;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// Verifies that implementing IHandlerConfiguration provides compile-time safe
/// Configure(HandlerChain) invocation, equivalent to the convention-based approach.
/// </summary>
public class can_customize_handler_chain_through_IHandlerConfiguration : IntegrationContext
{
    public can_customize_handler_chain_through_IHandlerConfiguration(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public void the_configure_method_via_interface_is_found_and_used()
    {
        chainFor<InterfaceConfiguredMessage>().ShouldBeWrappedWith<CustomFrame>();
    }

    [Fact]
    public void convention_based_configure_still_works()
    {
        // The original convention-based approach (without the interface) must continue to work
        chainFor<SpecialMessage>().ShouldBeWrappedWith<CustomFrame>();
    }
}

public class InterfaceConfiguredMessage;

#region sample_customized_handler_using_ihandlerconfiguration
public class InterfaceConfiguredHandler : IHandlerConfiguration
{
    public void Handle(InterfaceConfiguredMessage message)
    {
        // handle the message
    }

    public static void Configure(HandlerChain chain)
    {
        chain.Middleware.Add(new CustomFrame());

        chain.SuccessLogLevel = LogLevel.None;
        chain.ProcessingLogLevel = LogLevel.None;
    }
}

#endregion
