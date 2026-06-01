using IntegrationTests;
using JasperFx.CodeGeneration.Model;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests;

/// <summary>
/// GH-3001: when a handler chain falls back to service location, a dependency that takes
/// <see cref="IDocumentSession"/> must receive the SAME outbox-enrolled session the handler is
/// using — not a separate, un-enrolled one (which would defeat the transaction boundary). Proven via
/// reference equality against the handler's own session.
/// </summary>
public class service_location_document_session : PostgresqlContext
{
    [Fact]
    public async Task service_located_session_is_same_instance_as_the_handler_session()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString).IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(SessionProbeCommandHandler));

                opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;
                opts.Services.AddScoped<SessionCapturingService>();

                // Force the capturing service to be resolved via service location so the chain creates
                // a child scope — the path GH-3001 primes.
                opts.CodeGeneration.AlwaysUseServiceLocationFor<SessionCapturingService>();
            }).StartAsync();

        SessionIdentityProbe.Reset();

        await host.InvokeMessageAndWaitAsync(new SessionProbeCommand());

        SessionIdentityProbe.HandlerSession.ShouldNotBeNull();
        SessionIdentityProbe.ServiceLocatedSession.ShouldNotBeNull();

        // Reference equality — the service-located session IS the handler's outbox-enrolled session.
        ReferenceEquals(SessionIdentityProbe.HandlerSession, SessionIdentityProbe.ServiceLocatedSession)
            .ShouldBeTrue();
    }
}

public record SessionProbeCommand;

public static class SessionIdentityProbe
{
    public static IDocumentSession? HandlerSession;
    public static IDocumentSession? ServiceLocatedSession;

    public static void Reset()
    {
        HandlerSession = null;
        ServiceLocatedSession = null;
    }
}

public class SessionCapturingService(IDocumentSession session)
{
    public IDocumentSession Capture() => session;
}

public static class SessionProbeCommandHandler
{
    public static void Handle(SessionProbeCommand command, IDocumentSession handlerSession, IServiceProvider services)
    {
        SessionIdentityProbe.HandlerSession = handlerSession;
        SessionIdentityProbe.ServiceLocatedSession = services
            .GetRequiredService<SessionCapturingService>()
            .Capture();
    }
}
