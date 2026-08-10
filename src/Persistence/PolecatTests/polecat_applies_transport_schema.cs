using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Polecat;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport;

namespace PolecatTests;

/// <summary>
/// GH-3884, the mirror image of GH-3883 on the Marten side (see
/// MartenTests.marten_does_not_clobber_transport_schema). <c>PolecatIntegration.TransportSchemaName</c>
/// was public and documented but never read by anything, so an explicit assignment silently did
/// nothing and the SQL Server transport's queue tables stayed wherever the transport itself was
/// configured. Now the integration stamps an explicitly assigned schema onto the registered SQL
/// Server transport at host build (authoritative, like the Marten twin), while an unconfigured
/// default never overwrites an explicit
/// <c>UseSqlServerPersistenceAndTransport(..., transportSchema: ...)</c>.
/// </summary>
public class polecat_applies_transport_schema
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

    private static SqlServerTransport transportOf(IHost host)
    {
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        return runtime.Options.Transports.OfType<SqlServerTransport>().Single();
    }

    [Fact]
    public void an_explicit_schema_on_the_polecat_integration_is_applied_to_the_transport()
    {
        // The heart of GH-3884 — this assignment used to be inert.
        using var host = buildHost(opts =>
        {
            opts.UseSqlServerPersistenceAndTransport(
                    Servers.SqlServerConnectionString,
                    "myapp",
                    "myapp_queues",
                    MessageStoreRole.Ancillary)
                .AutoProvision();

            opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "polecat3884";
                })
                .IntegrateWithWolverine(x => x.TransportSchemaName = "polecat_chosen");
        });

        transportOf(host).TransportSchemaName.ShouldBe("polecat_chosen");
    }

    [Fact]
    public void an_explicit_transport_schema_survives_polecat_integration_when_unset()
    {
        using var host = buildHost(opts =>
        {
            opts.UseSqlServerPersistenceAndTransport(
                    Servers.SqlServerConnectionString,
                    "myapp",
                    "myapp_queues",
                    MessageStoreRole.Ancillary)
                .AutoProvision();

            opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "polecat3884";
                })
                .IntegrateWithWolverine();
        });

        transportOf(host).TransportSchemaName.ShouldBe("myapp_queues");
    }

    [Fact]
    public void the_explicit_integration_schema_wins_regardless_of_registration_order()
    {
        using var host = buildHost(opts =>
        {
            opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "polecat3884";
                })
                .IntegrateWithWolverine(x => x.TransportSchemaName = "polecat_chosen");

            opts.UseSqlServerPersistenceAndTransport(
                    Servers.SqlServerConnectionString,
                    "myapp",
                    "myapp_queues",
                    MessageStoreRole.Ancillary)
                .AutoProvision();
        });

        transportOf(host).TransportSchemaName.ShouldBe("polecat_chosen");
    }

    [Fact]
    public void the_transport_default_is_unchanged_when_nobody_configures_a_schema()
    {
        using var host = buildHost(opts =>
        {
            opts.UseSqlServerPersistenceAndTransport(
                Servers.SqlServerConnectionString,
                role: MessageStoreRole.Ancillary);

            opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "polecat3884";
                })
                .IntegrateWithWolverine();
        });

        // The SQL Server transport's own default ("dbo" via DatabaseSettings) is left alone.
        transportOf(host).TransportSchemaName.ShouldBe("dbo");
    }

    [Fact]
    public void an_explicit_message_storage_schema_is_stamped_onto_the_transport()
    {
        // Mirrors MartenIntegration.Configure(): the transport's envelope-table SQL must agree
        // with where the integration actually places the message storage.
        using var host = buildHost(opts =>
        {
            opts.UseSqlServerPersistenceAndTransport(
                    Servers.SqlServerConnectionString,
                    "myapp",
                    "myapp_queues",
                    MessageStoreRole.Ancillary)
                .AutoProvision();

            opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "polecat3884";
                })
                .IntegrateWithWolverine(x => x.MessageStorageSchemaName = "polecat_storage");
        });

        transportOf(host).MessageStorageSchemaName.ShouldBe("polecat_storage");
    }

    [Fact]
    public void the_integration_setting_is_inert_when_no_sql_server_transport_is_registered()
    {
        // A Polecat host with no SQL Server-backed queue endpoints has no transport tables to
        // place — the setting is simply a no-op, and no transport is conjured up.
        using var host = buildHost(opts =>
        {
            opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "polecat3884";
                })
                .IntegrateWithWolverine(x => x.TransportSchemaName = "polecat_chosen");
        });

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        runtime.Options.Transports.OfType<SqlServerTransport>().ShouldBeEmpty();
    }
}
