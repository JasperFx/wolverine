using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;

namespace MartenTests;

/// <summary>
/// GH-3883. <c>IntegrateWithWolverine()</c> is an <see cref="IWolverineExtension"/>, so its
/// Configure() runs at host build — AFTER the inline <c>UsePostgresqlPersistenceAndTransport(...)</c>
/// call in the same options lambda. It used to unconditionally assign its own
/// <c>TransportSchemaName</c> (default "wolverine_queues") onto the shared PostgreSQL transport, so
/// any host combining Marten with an explicitly-schema'd database transport silently lost the
/// schema it asked for: publishers wrote to one schema's queue table and listeners polled another's,
/// with no error on either side.
/// </summary>
public class marten_does_not_clobber_transport_schema : PostgresqlContext
{
    private static IHost buildHost(Action<WolverineOptions> configure)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;
                configure(opts);
            })
            .Build(); // Build, not Start — extensions apply at build and no database is touched.
    }

    private static PostgresqlTransport transportOf(IHost host)
    {
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        return runtime.Options.Transports.GetOrCreate<PostgresqlTransport>();
    }

    [Fact]
    public void an_explicit_transport_schema_survives_marten_integration()
    {
        using var host = buildHost(opts =>
        {
            opts.UsePostgresqlPersistenceAndTransport(
                    Servers.PostgresConnectionString,
                    "myapp",
                    "myapp_queues",
                    MessageStoreRole.Ancillary)
                .AutoProvision();

            opts.Services.AddMarten(m => m.Connection(Servers.PostgresConnectionString))
                .IntegrateWithWolverine();
        });

        transportOf(host).TransportSchemaName.ShouldBe("myapp_queues");
    }

    [Fact]
    public void the_explicit_schema_survives_regardless_of_registration_order()
    {
        using var host = buildHost(opts =>
        {
            opts.Services.AddMarten(m => m.Connection(Servers.PostgresConnectionString))
                .IntegrateWithWolverine();

            opts.UsePostgresqlPersistenceAndTransport(
                    Servers.PostgresConnectionString,
                    "myapp",
                    "myapp_queues",
                    MessageStoreRole.Ancillary)
                .AutoProvision();
        });

        transportOf(host).TransportSchemaName.ShouldBe("myapp_queues");
    }

    [Fact]
    public void an_explicit_schema_on_the_marten_integration_still_wins()
    {
        // The integration's own knob remains authoritative when the caller sets it — that is a
        // deliberate statement about where the queues go, not the default leaking through.
        using var host = buildHost(opts =>
        {
            opts.UsePostgresqlPersistenceAndTransport(
                    Servers.PostgresConnectionString,
                    "myapp",
                    "myapp_queues",
                    MessageStoreRole.Ancillary)
                .AutoProvision();

            opts.Services.AddMarten(m => m.Connection(Servers.PostgresConnectionString))
                .IntegrateWithWolverine(x => x.TransportSchemaName = "marten_chosen");
        });

        transportOf(host).TransportSchemaName.ShouldBe("marten_chosen");
    }

    [Fact]
    public void the_default_is_unchanged_when_nobody_configures_a_schema()
    {
        using var host = buildHost(opts =>
        {
            opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString);

            opts.Services.AddMarten(m => m.Connection(Servers.PostgresConnectionString))
                .IntegrateWithWolverine();
        });

        transportOf(host).TransportSchemaName.ShouldBe("wolverine_queues");
    }
}
