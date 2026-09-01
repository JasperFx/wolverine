using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Oracle;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Transports.Tcp;

namespace OracleTests;

/// <summary>
/// GH-4216. The whole shared suite under <see cref="MessageIdentity.IdAndDestination"/> rather than the
/// default <see cref="MessageIdentity.IdOnly"/>, which only PostgreSQL, SQL Server and RavenDb answered
/// before. GH-4209 was identity-shape specific end to end -- matching on <c>id</c> alone where the key is
/// <c>(id, received_at)</c> -- so a store that never runs the suite under the composite shape cannot report
/// that class of bug.
///
/// Oracle earns this more than the other providers: it diverges further from the shared RDBMS base than any
/// of them (<c>NUMBER</c> reads back as <c>Int64</c>, its own quoting rules, the URI casing of GH-3820), so
/// it is where a shared-base assumption is most likely to be wrong without anyone noticing.
/// </summary>
[Collection("oracle")]
public class OracleMessageStore_with_IdAndDestination_Identity : MessageStoreCompliance
{
    // Its own schema -- in Oracle a schema is a user, and the container's init script grants wolverine
    // CREATE USER precisely so tests can own separate ones. Keeps the composite-key DDL away from the
    // default-identity suite's WOLVERINE.
    private const string SchemaName = "WOLVERINE2";

    public override async Task<IHost> BuildCleanHost()
    {
        var dataSource = new OracleDataSource(Servers.OracleConnectionString);
        var settings = new DatabaseSettings
        {
            SchemaName = SchemaName,
            CommandQueuesEnabled = true,
            Role = MessageStoreRole.Main
        };
        var durabilitySettings = new DurabilitySettings { MessageIdentity = MessageIdentity.IdAndDestination };
        var store = new OracleMessageStore(settings, durabilitySettings, dataSource,
            NullLogger<OracleMessageStore>.Instance);

        await store.Admin.MigrateAsync();

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, SchemaName);
                opts.ListenAtPort(2346).UseDurableInbox();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;
            }).StartAsync();

        var hostStore = host.Get<IMessageStore>();
        await hostStore.Admin.ClearAllAsync();

        return host;
    }
}
