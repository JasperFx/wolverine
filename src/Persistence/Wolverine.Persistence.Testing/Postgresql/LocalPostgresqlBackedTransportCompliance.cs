using System.Threading.Tasks;
using IntegrationTests;
using TestingSupport.Compliance;
using Wolverine.Persistence.Postgresql;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Persistence.Testing.Postgresql;

public class LocalPostgresqlBackedFixture : SendingComplianceFixture, IAsyncLifetime
{
    public LocalPostgresqlBackedFixture() : base("local://one/durable".ToUri())
    {
    }

    public Task InitializeAsync()
    {
        return TheOnlyAppIs(opts => { opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString); });
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }
}

[Collection("marten")]
public class LocalPostgresqlBackedTransportCompliance : SendingCompliance<LocalPostgresqlBackedFixture>
{
}
