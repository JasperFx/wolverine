using IntegrationTests;
using JasperFx.Core;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Configuration;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace PostgresqlTests.Transport;

public class PostgresqlTransportDurableFixture : TransportComplianceFixture, IAsyncLifetime
{
    public PostgresqlTransportDurableFixture() : base("postgresql://receiver".ToUri(), 10)
    {
    }

    public async Task InitializeAsync()
    {
        await SenderIs(opts =>
        {
            opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "durable", transportSchema:"durable")
                .AutoProvision().AutoPurgeOnStartup();

            opts.ListenToPostgresqlQueue("sender");
            opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
        });

        await ReceiverIs(opts =>
        {
            opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "durable", transportSchema:"durable");

            opts.ListenToPostgresqlQueue("receiver").UseDurableInbox();
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

[Collection("marten")]
public class PostgresqlTransport_Durable_Compliance : TransportCompliance<PostgresqlTransportDurableFixture>;

public class PostgresqlTransportBufferedFixture : TransportComplianceFixture, IAsyncLifetime
{
    public PostgresqlTransportBufferedFixture() : base("postgresql://receiver".ToUri(), 10)
    {
    }

    public async Task InitializeAsync()
    {
        await SenderIs(opts =>
        {
            opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "buffered_compliance")
                .AutoProvision().AutoPurgeOnStartup().DisableInboxAndOutboxOnAll();

            #region sample_setting_postgres_queue_to_buffered

            opts.ListenToPostgresqlQueue("sender").BufferedInMemory();

            #endregion

        });

        await ReceiverIs(opts =>
        {
            opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "buffered_compliance")
                .AutoProvision().AutoPurgeOnStartup().DisableInboxAndOutboxOnAll();

            opts.ListenToPostgresqlQueue("receiver").BufferedInMemory();
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

[Collection("sqlserver")]
public class PostgresqlTransport_Buffered_Compliance : TransportCompliance<PostgresqlTransportBufferedFixture>
{
    [Fact]
    public void endpoints_are_all_buffered()
    {
        // theSender.GetRuntime().Endpoints.EndpointFor(new Uri("sqlserver://receiver")).Mode.ShouldBe(EndpointMode.BufferedInMemory);
        // theSender.GetRuntime().Endpoints.EndpointFor(new Uri("sqlserver://sender")).Mode.ShouldBe(EndpointMode.BufferedInMemory);
        //
        // theReceiver.GetRuntime().Endpoints.EndpointFor(new Uri("sqlserver://receiver")).Mode.ShouldBe(EndpointMode.BufferedInMemory);
        theReceiver.GetRuntime().Endpoints.GetOrBuildSendingAgent(new Uri("postgresql://sender")).Endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }
}