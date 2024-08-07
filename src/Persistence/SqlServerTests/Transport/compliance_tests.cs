using IntegrationTests;
using JasperFx.Core;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace SqlServerTests.Transport;

public class SqlTransportDurableFixture : TransportComplianceFixture, IAsyncLifetime
{
    public SqlTransportDurableFixture() : base("sqlserver://receiver".ToUri(), 10)
    {
    }

    public async Task InitializeAsync()
    {
        await SenderIs(opts =>
        {
            opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "durable")
                .AutoProvision().AutoPurgeOnStartup();

            opts.ListenToSqlServerQueue("sender");
            opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

            opts.Durability.Mode = DurabilityMode.Solo;
        });

        await ReceiverIs(opts =>
        {
            opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "durable");

            opts.ListenToSqlServerQueue("receiver").UseDurableInbox();
            opts.Durability.Mode = DurabilityMode.Solo;
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

[Collection("sqlserver")]
public class SqlServerTransport_Durable_Compliance : TransportCompliance<SqlTransportDurableFixture>;

public class SqlTransportBufferedFixture : TransportComplianceFixture, IAsyncLifetime
{
    public SqlTransportBufferedFixture() : base("sqlserver://receiver".ToUri(), 10)
    {
    }

    public async Task InitializeAsync()
    {
        await SenderIs(opts =>
        {
            opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "buffered_compliance")
                .AutoProvision().AutoPurgeOnStartup().DisableInboxAndOutboxOnAll();

            #region sample_setting_sql_server_queue_to_buffered

            opts.ListenToSqlServerQueue("sender").BufferedInMemory();

            #endregion

            opts.Durability.Mode = DurabilityMode.Solo;

        });

        await ReceiverIs(opts =>
        {
            opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "buffered_compliance")
                .AutoProvision().AutoPurgeOnStartup().DisableInboxAndOutboxOnAll();

            opts.ListenToSqlServerQueue("receiver").BufferedInMemory();

            opts.Durability.Mode = DurabilityMode.Solo;
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

[Collection("sqlserver")]
public class SqlServerTransport_Buffered_Compliance : TransportCompliance<SqlTransportBufferedFixture>
{
    [Fact]
    public void endpoints_are_all_buffered()
    {
        // theSender.GetRuntime().Endpoints.EndpointFor(new Uri("sqlserver://receiver")).Mode.ShouldBe(EndpointMode.BufferedInMemory);
        // theSender.GetRuntime().Endpoints.EndpointFor(new Uri("sqlserver://sender")).Mode.ShouldBe(EndpointMode.BufferedInMemory);
        //
        // theReceiver.GetRuntime().Endpoints.EndpointFor(new Uri("sqlserver://receiver")).Mode.ShouldBe(EndpointMode.BufferedInMemory);
        theReceiver.GetRuntime().Endpoints.GetOrBuildSendingAgent(new Uri("sqlserver://sender")).Endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }
}