using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// GH-4171. The GH-3001 scope priming was composed as a frame appended to each chain, which looked for
/// the scoped <see cref="IServiceProvider"/> during the arranger's first resolution pass. The scope for
/// an opaque scoped/transient registration is not created until after that pass, so for any chain whose
/// only reason to service-locate was such a registration, the frame found nothing, attached nothing,
/// and said nothing — a service-located <see cref="IMessageContext"/> was a second, un-enrolled context.
///
/// Every test in <see cref="service_location_message_context"/> happens to put an
/// <see cref="IServiceProvider"/> on the handler signature, which is exactly the shape that did work,
/// and is why this went unnoticed. This handler has no <see cref="IServiceProvider"/> at all.
/// </summary>
public class scope_priming_without_an_explicit_service_provider
{
    [Fact]
    public async Task a_service_located_context_is_the_handlers_own_context()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;

                // Opaque scoped lambda: the container is the only thing that can build it, so the chain
                // drops onto service location without anything naming an IServiceProvider.
                opts.Services.AddScoped<IOpaqueContextProbe>(sp =>
                    new OpaqueContextProbe(sp.GetRequiredService<IMessageContext>()));
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        OpaqueProbeState.Reset();

        await host.InvokeMessageAndWaitAsync(new OpaqueProbeCommand());

        OpaqueProbeState.HandlerContext.ShouldNotBeNull();
        OpaqueProbeState.LocatedContext.ShouldNotBeNull();

        // Reference equality: the located instance IS the handler's, enrolled with the active outbox.
        ReferenceEquals(OpaqueProbeState.HandlerContext, OpaqueProbeState.LocatedContext).ShouldBeTrue();
    }
}

public record OpaqueProbeCommand;

public interface IOpaqueContextProbe
{
    IMessageContext Context { get; }
}

public class OpaqueContextProbe(IMessageContext context) : IOpaqueContextProbe
{
    public IMessageContext Context { get; } = context;
}

public static class OpaqueProbeState
{
    public static MessageContext? HandlerContext;
    public static IMessageContext? LocatedContext;

    public static void Reset()
    {
        HandlerContext = null;
        LocatedContext = null;
    }
}

public static class OpaqueProbeCommandHandler
{
    // No IServiceProvider on the signature — the opaque IOpaqueContextProbe registration is the only
    // thing forcing the scope, and that scope used to go unprimed.
    public static void Handle(OpaqueProbeCommand command, MessageContext handlerContext, IOpaqueContextProbe probe)
    {
        OpaqueProbeState.HandlerContext = handlerContext;
        OpaqueProbeState.LocatedContext = probe.Context;
    }
}
