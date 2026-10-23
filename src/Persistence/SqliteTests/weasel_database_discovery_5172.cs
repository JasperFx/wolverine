using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Weasel.Core;
using Weasel.Core.Migrations;
using Wolverine.Sqlite;

namespace SqliteTests;

/// <summary>
/// GH-5172. The SQLite twin of the Oracle test: after PersistMessagesWithSqlite(), the Weasel db-* commands
/// find the message store through IDatabaseSource without the host having been started.
/// </summary>
public class weasel_database_discovery_5172 : IAsyncLifetime
{
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase("weasel_discovery_5172");
    private IHost theHost = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        // Built, but not started -- which is all the db-* commands do
        theHost = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(_database.ConnectionString);
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            }).Build();

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        theHost.Dispose();
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task the_sqlite_message_store_is_discovered_and_can_be_applied()
    {
        var databases = new List<IDatabase>();
        foreach (var source in theHost.Services.GetServices<IDatabaseSource>())
        {
            databases.AddRange(await source.BuildDatabases());
        }

        var database = databases.ShouldHaveSingleItem();

        database.ToDatabaseScript().ShouldContain("wolverine_incoming_envelopes", Case.Insensitive);

        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: Token);
        await database.AssertDatabaseMatchesConfigurationAsync(Token);
    }
}
