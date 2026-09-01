using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Transports.Tcp;

namespace MySqlTests;

/// <summary>
/// GH-4216. The whole shared suite under <see cref="MessageIdentity.IdAndDestination"/> rather than the
/// default <see cref="MessageIdentity.IdOnly"/>, which only PostgreSQL, SQL Server and RavenDb answered
/// before. GH-4209 was identity-shape specific end to end -- matching on <c>id</c> alone where the key is
/// <c>(id, received_at)</c> -- so a store that never runs the suite under the composite shape cannot report
/// that class of bug.
/// </summary>
[Collection("mysql")]
public class MySqlMessageStore_with_IdAndDestination_Identity : MessageStoreCompliance
{
    // A MySQL schema IS a database (GH-3815), so this is a wholly separate database from the
    // default-identity suite's "receiver" -- the two key shapes never share DDL.
    private const string SchemaName = "receiver2";

    public override async Task<IHost> BuildCleanHost()
    {
        // Migrate directly first, exactly as the default-identity suite does, so the node agent never
        // races the host against a schema that does not exist yet.
        var dataSource = MySqlDataSourceFactory.Create(Servers.MySqlConnectionString);
        var settings = new DatabaseSettings
        {
            SchemaName = SchemaName,
            CommandQueuesEnabled = true,
            Role = MessageStoreRole.Main
        };
        var durabilitySettings = new DurabilitySettings { MessageIdentity = MessageIdentity.IdAndDestination };
        var store = new MySqlMessageStore(settings, durabilitySettings, dataSource,
            NullLogger<MySqlMessageStore>.Instance);

        await store.Admin.MigrateAsync();

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, SchemaName);
                opts.ListenAtPort(2346).UseDurableInbox();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;
            }).StartAsync();

        var hostStore = host.Get<IMessageStore>();
        await hostStore.Admin.ClearAllAsync();

        return host;
    }
}
