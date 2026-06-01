using IntegrationTests;
using JasperFx.CodeGeneration.Model;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;
using Xunit;

namespace PolecatTests;

/// <summary>
/// GH-3001: when a handler chain falls back to service location, a dependency that takes Polecat's
/// <see cref="IDocumentSession"/> must receive the SAME outbox-enrolled session the handler is using
/// — not a separate, un-enrolled one (which would defeat the transaction boundary). Proven via
/// reference equality against the handler's own session.
/// </summary>
public class service_location_document_session : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(SessionProbeCommandHandler));

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "scope_priming";
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();

                opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;
                opts.Services.AddScoped<SessionCapturingService>();

                // Force the capturing service to be resolved via service location so the chain creates
                // a child scope — the path GH-3001 primes.
                opts.CodeGeneration.AlwaysUseServiceLocationFor<SessionCapturingService>();
            }).StartAsync();
    }

    public async Task DisposeAsync() => await _host.StopAsync();

    [Fact]
    public async Task service_located_session_is_same_instance_as_the_handler_session()
    {
        SessionIdentityProbe.Reset();

        await _host.InvokeMessageAndWaitAsync(new SessionProbeCommand());

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
