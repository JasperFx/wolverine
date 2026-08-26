using IntegrationTests;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests;

/// <summary>
/// GH-4145 (the GH-3001 pattern, ported from Wolverine.Marten): when a handler chain falls back to
/// service location, a dependency that takes <see cref="IDocumentSession"/> must receive the SAME
/// outbox-enrolled session the handler is using — not a separate, un-enrolled one (which would defeat
/// the transaction boundary). Proven via reference equality against the handler's own session.
/// </summary>
public class service_location_document_session : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "pc_service_location";
                }).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(PcSessionProbeCommandHandler));

                opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;
                opts.Services.AddScoped<PcSessionCapturingService>();

                // Force the capturing service to be resolved via service location so the chain creates
                // a child scope — the path GH-4145 primes.
                opts.CodeGeneration.AlwaysUseServiceLocationFor<PcSessionCapturingService>();
            }).StartAsync();

        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)store).Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task service_located_session_is_same_instance_as_the_handler_session()
    {
        PcSessionIdentityProbe.Reset();

        var command = new PcSessionProbeCommand(Guid.NewGuid());
        await _host.InvokeMessageAndWaitAsync(command);

        PcSessionIdentityProbe.HandlerSession.ShouldNotBeNull();
        PcSessionIdentityProbe.ServiceLocatedSession.ShouldNotBeNull();

        // Reference equality — the service-located session IS the handler's outbox-enrolled session.
        ReferenceEquals(PcSessionIdentityProbe.HandlerSession, PcSessionIdentityProbe.ServiceLocatedSession)
            .ShouldBeTrue();

        // And the guarantee that identity is standing in for: the handler never touched the session
        // itself, so the document only lands if the write the service-located session took is inside
        // the transaction the middleware commits.
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using var query = store.QuerySession();
        (await query.LoadAsync<PcProbeDoc>(command.Id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task ordinary_di_scope_gets_a_genuine_scoped_session()
    {
        // CreateAsyncScope, not CreateScope: a Polecat session is IAsyncDisposable only, and a
        // synchronously disposed scope throws rather than disposing it.
        await using var scope = _host.Services.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.ShouldNotBeNull();

        // Scoped, so the same instance within one scope...
        scope.ServiceProvider.GetRequiredService<IDocumentSession>().ShouldBeSameAs(session);

        // ...and a genuinely different one in another.
        await using var other = _host.Services.CreateAsyncScope();
        other.ServiceProvider.GetRequiredService<IDocumentSession>().ShouldNotBeSameAs(session);
    }
}

public record PcSessionProbeCommand(Guid Id);

public class PcProbeDoc
{
    public Guid Id { get; set; }
}

public static class PcSessionIdentityProbe
{
    public static IDocumentSession? HandlerSession;
    public static IDocumentSession? ServiceLocatedSession;

    public static void Reset()
    {
        HandlerSession = null;
        ServiceLocatedSession = null;
    }
}

public class PcSessionCapturingService(IDocumentSession session)
{
    public IDocumentSession Capture() => session;

    public void Store(PcProbeDoc doc) => session.Store(doc);
}

public static class PcSessionProbeCommandHandler
{
    public static void Handle(PcSessionProbeCommand command, IDocumentSession handlerSession,
        IServiceProvider services)
    {
        var located = services.GetRequiredService<PcSessionCapturingService>();

        PcSessionIdentityProbe.HandlerSession = handlerSession;
        PcSessionIdentityProbe.ServiceLocatedSession = located.Capture();

        // Deliberately writing through the SERVICE-LOCATED session and nothing else
        located.Store(new PcProbeDoc { Id = command.Id });
    }
}
