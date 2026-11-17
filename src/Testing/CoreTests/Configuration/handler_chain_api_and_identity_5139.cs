using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;
using Wolverine.Util;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-5139. HandlerChain 1:1, steps 7-8: looking up the chains of a message type handled by several handler
/// types, the handler type on the capabilities descriptor, and the startup warning for durable broker fan-out
/// under MessageIdentity.IdOnly
/// </summary>
public class handler_chain_api_and_identity_5139
{
    private static Task<IHost> startHost(Action<WolverineOptions>? configure = null)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<ApiIdentityShippingHandler>()
                    .IncludeType<ApiIdentityBillingHandler>()
                    .IncludeType<ApiIdentitySingleHandler>();

                configure?.Invoke(opts);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static HandlerGraph handlersOf(IHost host)
        => host.Services.GetRequiredService<IWolverineRuntime>().As<WolverineRuntime>().Handlers;

    [Fact]
    public async Task chains_for_returns_one_chain_per_handler_type()
    {
        using var host = await startHost();
        var handlers = handlersOf(host);

        #region sample_looking_up_the_chains_of_a_message_type
        // Every chain of a message type, one per handler type
        var chains = handlers.ChainsFor<ApiIdentityOrderPlaced>();

        // The one chain of a message type that belongs to a handler type
        var shipping = handlers.ChainFor<ApiIdentityOrderPlaced, ApiIdentityShippingHandler>();
        #endregion

        chains.Count.ShouldBe(2);
        chains.ShouldContain(shipping!);

        handlers.ChainFor(typeof(ApiIdentityOrderPlaced), typeof(ApiIdentityBillingHandler))!
            .Handlers.Single().HandlerType.ShouldBe(typeof(ApiIdentityBillingHandler));
    }

    [Fact]
    public async Task chain_for_a_handler_type_that_does_not_handle_the_message_is_null()
    {
        using var host = await startHost();
        var handlers = handlersOf(host);

        handlers.ChainFor(typeof(ApiIdentityOrderPlaced), typeof(ApiIdentitySingleHandler)).ShouldBeNull();
        handlers.ChainsFor(typeof(ApiIdentityUnhandled)).ShouldBeEmpty();
    }

    [Fact]
    public async Task chains_for_a_single_handler_message_is_the_one_chain_for_it()
    {
        using var host = await startHost();
        var handlers = handlersOf(host);

        handlers.ChainsFor<ApiIdentitySingle>().Single().ShouldBeSameAs(handlers.ChainFor<ApiIdentitySingle>());
    }

    [Fact]
    public async Task the_ambiguity_error_points_at_the_new_lookups()
    {
        using var host = await startHost();

        var ex = Should.Throw<AmbiguousHandlerChainException>(() =>
            handlersOf(host).ChainFor(typeof(ApiIdentityOrderPlaced)));

        ex.Message.ShouldContain("ChainsFor");
        ex.Message.ShouldContain("ChainFor(messageType, handlerType)");
    }

    [Fact]
    public async Task the_message_descriptor_has_one_handler_descriptor_per_handler_type_with_its_type()
    {
        using var host = await startHost();
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        var descriptor = new MessageDescriptor(typeof(ApiIdentityOrderPlaced), runtime);

        descriptor.Handlers.Count.ShouldBe(2);
        descriptor.Handlers.Select(x => x.HandlerType!.FullName).OrderBy(x => x).ShouldBe([
            typeof(ApiIdentityBillingHandler).FullName!,
            typeof(ApiIdentityShippingHandler).FullName!
        ]);

        foreach (var handler in descriptor.Handlers)
        {
            handler.Handlers.Single().Type.FullName.ShouldBe(handler.HandlerType!.FullName);
        }
    }

    [Fact]
    public async Task two_handler_types_on_durable_external_listeners_under_IdOnly_are_warned_about()
    {
        using var host = await startHost(opts =>
        {
            opts.ListenAtPort(PortFinder.GetAvailablePort()).Named("api-identity-shipping").UseDurableInbox()
                .AddStickyHandler(typeof(ApiIdentityShippingHandler));
            opts.ListenAtPort(PortFinder.GetAvailablePort()).Named("api-identity-billing").UseDurableInbox()
                .AddStickyHandler(typeof(ApiIdentityBillingHandler));
        });
        var handlers = handlersOf(host);

        // No message store in this host, so startup does not run the check; ask it directly
        var message = WolverineRuntime.DurableFanOutWithoutMessageIdentity(handlers.Groups, MessageIdentity.IdOnly);

        message.ShouldNotBeNull();
        message.ShouldContain(typeof(ApiIdentityOrderPlaced).FullNameInCode());
        message.ShouldContain(typeof(ApiIdentityShippingHandler).FullNameInCode());
        message.ShouldContain(typeof(ApiIdentityBillingHandler).FullNameInCode());
        message.ShouldContain("MessageIdentity.IdAndDestination");

        WolverineRuntime.DurableFanOutWithoutMessageIdentity(handlers.Groups, MessageIdentity.IdAndDestination)
            .ShouldBeNull();
    }

    [Fact]
    public async Task buffered_external_listeners_are_not_warned_about()
    {
        using var host = await startHost(opts =>
        {
            opts.ListenAtPort(PortFinder.GetAvailablePort()).Named("api-identity-shipping").BufferedInMemory()
                .AddStickyHandler(typeof(ApiIdentityShippingHandler));
            opts.ListenAtPort(PortFinder.GetAvailablePort()).Named("api-identity-billing").BufferedInMemory()
                .AddStickyHandler(typeof(ApiIdentityBillingHandler));
        });

        WolverineRuntime.DurableFanOutWithoutMessageIdentity(handlersOf(host).Groups, MessageIdentity.IdOnly)
            .ShouldBeNull();
    }

    [Fact]
    public async Task durable_local_queues_are_not_warned_about()
    {
        // No sticky listeners at all: each handler type gets its own local queue, and a local route always
        // sends an envelope with an id of its own
        using var host = await startHost(opts => opts.Policies.UseDurableLocalQueues());

        WolverineRuntime.DurableFanOutWithoutMessageIdentity(handlersOf(host).Groups, MessageIdentity.IdOnly)
            .ShouldBeNull();
    }
}

public record ApiIdentityOrderPlaced(Guid Id);

public record ApiIdentitySingle(Guid Id);

public record ApiIdentityUnhandled(Guid Id);

[WolverineIgnore]
public class ApiIdentityShippingHandler
{
    public void Handle(ApiIdentityOrderPlaced message)
    {
    }
}

[WolverineIgnore]
public class ApiIdentityBillingHandler
{
    public void Handle(ApiIdentityOrderPlaced message)
    {
    }
}

[WolverineIgnore]
public class ApiIdentitySingleHandler
{
    public void Handle(ApiIdentitySingle message)
    {
    }
}
