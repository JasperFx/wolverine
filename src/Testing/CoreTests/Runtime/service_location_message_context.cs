using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// Verifies the AsyncLocal-based handoff that keeps service-located <see cref="IMessageBus"/>
/// / <see cref="IMessageContext"/> instances pointed at the same MessageContext the handler
/// itself received. Without this, a bus pulled from a constructor on a service the user
/// injects bypasses the active outbox. See issue #2583.
/// </summary>
public class service_location_message_context
{
    [Fact]
    public async Task service_located_bus_publishes_through_active_context_when_chain_uses_service_location()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;
                opts.Services.AddTransient<BusUsingService>();

                // Force BusUsingService to be resolved via service location (rather than
                // codegen-injected) so the chain is flagged UsesServiceLocation = true and
                // the ServiceLocationAwareExecutor wraps it. Mirrors the pattern in the
                // existing service_location_assertions tests.
                opts.CodeGeneration.AlwaysUseServiceLocationFor<BusUsingService>();
            }).StartAsync();

        var session = await host.TrackActivity().IncludeExternalTransports().ExecuteAndWaitAsync(c =>
            c.PublishAsync(new ServiceLocatedBusCommand("hello")));

        // The cascading message published *via the service-located IMessageBus* must have been
        // tracked through the same MessageContext the handler received — otherwise the tracking
        // session would never see it.
        session.Sent.SingleEnvelope<ServiceLocatedBusEcho>().Message
            .ShouldBeOfType<ServiceLocatedBusEcho>()
            .Payload.ShouldBe("hello");
    }

    [Fact]
    public async Task chain_without_service_location_does_not_set_message_context_current()
    {
        // A chain that doesn't service-locate must not touch MessageContext.Current during
        // invocation — that's how we keep AsyncLocal overhead off the hot path. Verified
        // by capturing Current from inside the handler; it must be null.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;
            }).StartAsync();

        CleanCommandProbe.Reset();

        await host.InvokeMessageAndWaitAsync(new CleanCommand());

        CleanCommandProbe.WasInvoked.ShouldBeTrue();
        CleanCommandProbe.CurrentDuringInvocation.ShouldBeNull();
    }

    [Fact]
    public async Task message_context_current_is_null_outside_handler_invocation()
    {
        // Sanity check: the AsyncLocal default is null, so service resolution outside any
        // handler invocation must hit the fall-back factory branch.
        MessageContext.Current.ShouldBeNull();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();

        var bus = host.MessageBus();
        bus.ShouldNotBeNull();
    }

    [Fact]
    public async Task service_located_message_context_is_same_instance_as_handler_argument()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;
                opts.Services.AddTransient<ContextCapturingService>();

                // Force the capturing service to be resolved via service location so the
                // chain is flagged UsesServiceLocation = true.
                opts.CodeGeneration.AlwaysUseServiceLocationFor<ContextCapturingService>();
            }).StartAsync();

        ContextIdentityProbe.Reset();

        await host.InvokeMessageAndWaitAsync(new IdentityProbeCommand());

        ContextIdentityProbe.HandlerContext.ShouldNotBeNull();
        ContextIdentityProbe.ServiceLocatedContext.ShouldNotBeNull();
        // Reference equality — proves the service-located instance IS the handler's instance.
        ReferenceEquals(ContextIdentityProbe.HandlerContext, ContextIdentityProbe.ServiceLocatedContext)
            .ShouldBeTrue();
    }
}

#region Service-location-aware test fixtures

public record ServiceLocatedBusCommand(string Payload);
public record ServiceLocatedBusEcho(string Payload);

/// <summary>
/// Service-located access to <see cref="IMessageBus"/> via constructor injection on a
/// transient service that the handler resolves at runtime via <c>IServiceProvider</c>.
/// </summary>
public class BusUsingService(IMessageBus bus)
{
    public IMessageBus Bus => bus;

    public ValueTask EchoAsync(string payload) =>
        Bus.PublishAsync(new ServiceLocatedBusEcho(payload));
}

public static class ServiceLocatedBusCommandHandler
{
    // Resolves BusUsingService from the IServiceProvider — service location of an
    // IMessageBus-consuming service. The handler's own IServiceProvider use is what
    // triggers the chain to be marked UsesServiceLocation = true at codegen time.
    public static async Task Handle(ServiceLocatedBusCommand command, IServiceProvider services)
    {
        var svc = services.GetRequiredService<BusUsingService>();
        await svc.EchoAsync(command.Payload);
    }
}

public static class ServiceLocatedBusEchoHandler
{
    // No-op handler so the cascaded echo lands somewhere; the test asserts on what was sent,
    // not what was processed, but having a registered handler keeps tracking happy.
    public static void Handle(ServiceLocatedBusEcho echo) { }
}

#endregion

#region Plain (no service location) chain

public record CleanCommand;

public static class CleanCommandProbe
{
    public static bool WasInvoked;
    public static MessageContext? CurrentDuringInvocation;

    public static void Reset()
    {
        WasInvoked = false;
        CurrentDuringInvocation = null;
    }
}

public static class CleanCommandHandler
{
    public static void Handle(CleanCommand cmd)
    {
        CleanCommandProbe.WasInvoked = true;
        CleanCommandProbe.CurrentDuringInvocation = MessageContext.Current;
    }
}

#endregion

#region Identity probe

public record IdentityProbeCommand;

public static class ContextIdentityProbe
{
    public static MessageContext? HandlerContext;
    public static IMessageContext? ServiceLocatedContext;

    public static void Reset()
    {
        HandlerContext = null;
        ServiceLocatedContext = null;
    }
}

public class ContextCapturingService(IMessageContext context)
{
    public IMessageContext Capture() => context;
}

public static class IdentityProbeCommandHandler
{
    public static void Handle(IdentityProbeCommand cmd, MessageContext handlerContext, IServiceProvider services)
    {
        ContextIdentityProbe.HandlerContext = handlerContext;
        ContextIdentityProbe.ServiceLocatedContext = services
            .GetRequiredService<ContextCapturingService>()
            .Capture();
    }
}

#endregion
