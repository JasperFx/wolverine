using IntegrationTests;
using JasperFx.Core;
using Wolverine.ComplianceTests.Compliance;
using Wolverine;
using Wolverine.Postgresql;

namespace PostgresqlTests;

public class LocalPostgresqlBackedFixture : TransportComplianceFixture, IAsyncLifetime
{
    public LocalPostgresqlBackedFixture() : base("local://one/durable".ToUri())
    {
    }

    public async ValueTask InitializeAsync()
    {
        await TheOnlyAppIs(opts =>
        {
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString);
            opts.Durability.Mode = DurabilityMode.Solo;
        });
    }

}

[Collection("marten")]
public class LocalPostgresqlBackedTransportCompliance : TransportCompliance<LocalPostgresqlBackedFixture>;