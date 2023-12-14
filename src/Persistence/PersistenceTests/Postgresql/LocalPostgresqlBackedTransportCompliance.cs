using IntegrationTests;
using JasperFx.Core;
using TestingSupport.Compliance;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Util;
using Xunit;

namespace PersistenceTests.Postgresql;

public class LocalPostgresqlBackedFixture : TransportComplianceFixture, IAsyncLifetime
{
    public LocalPostgresqlBackedFixture() : base("local://one/durable".ToUri())
    {
    }

    public Task InitializeAsync()
    {
        return TheOnlyAppIs(opts =>
        {
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString);
            opts.Durability.Mode = DurabilityMode.Solo;
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

[Collection("marten")]
public class LocalPostgresqlBackedTransportCompliance : TransportCompliance<LocalPostgresqlBackedFixture>
{
}