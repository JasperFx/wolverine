using Fisher;
using JasperFx;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace FisherTests;

/// <summary>
///     GH-3907: the provider-agnostic <c>[Storage(typeof(IMyStore))]</c> attribute must route a handler
///     to a Fisher <b>ancillary</b> store, resolving the Fisher
///     <see cref="Wolverine.Persistence.IAncillaryStoreFrameProvider" /> from the store marker type — the
///     same coverage the Marten and Polecat suites give.
/// </summary>
/// <remarks>
///     This is the case that made ancillary support non-optional. CritterWatch registers its own store
///     (<c>ICritterWatchStore</c>) as an ancillary store rather than the application's primary one, so
///     without this a store-agnostic consumer can resolve the abstractions for the host application's
///     store but not for its own — which defeats the decoupling the whole exercise exists for.
///     <para>
///     Each store is its own SQLite <b>file</b>, which is the shape that gets two concurrent writers out
///     of SQLite rather than contending on one.
///     </para>
/// </remarks>
public class storage_attribute_routes_to_fisher_store : IAsyncLifetime
{
    private FisherTestDatabase theAncillaryDatabase = null!;
    private FisherTestDatabase theMainDatabase = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theMainDatabase = Servers.CreateDatabase("storage_attr_main");
        theAncillaryDatabase = Servers.CreateDatabase("storage_attr_players");

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RecordPlayerScoreHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theMainDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.Services.AddFisherStore<IPlayerStore>(m =>
                    {
                        m.Connection(theAncillaryDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theMainDatabase.Dispose();
        theAncillaryDatabase.Dispose();
    }

    [Fact]
    public async Task the_work_commits_through_the_targeted_store_and_not_the_main_one()
    {
        await theHost.InvokeMessageAndWaitAsync(new RecordPlayerScore("kasey", 42));

        // ...landed in the ancillary store
        await using (var players = theHost.Services.GetRequiredService<IPlayerStore>().LightweightSession())
        {
            var player = await players.LoadAsync<Player>("kasey", TestContext.Current.CancellationToken);
            player.ShouldNotBeNull();
            player.Score.ShouldBe(42);
        }

        // ...and demonstrably NOT in the main one. Asserting the negative is the point: a [Storage]
        // that silently fell back to the primary session would still satisfy the assertion above.
        //
        // Asked of sqlite_master rather than through a session, because on Fisher a document type
        // whose table was never created throws "no such table" rather than returning null - so the
        // absence of the TABLE is both the stronger claim and the one that does not need a catch.
        (await tableExistsAsync(theMainDatabase, "fi_doc_player")).ShouldBeFalse();
        (await tableExistsAsync(theAncillaryDatabase, "fi_doc_player")).ShouldBeTrue();
    }

    private static async Task<bool> tableExistsAsync(FisherTestDatabase database, string table)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from sqlite_master where type = 'table' and name = $name";
        command.Parameters.AddWithValue("$name", table);

        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)) > 0;
    }
}

public interface IPlayerStore : IDocumentStore;

public class Player
{
    public string Id { get; set; } = null!;
    public int Score { get; set; }
}

public record RecordPlayerScore(string Name, int Score);

public static class RecordPlayerScoreHandler
{
    // [Storage] names the store by its marker interface and nothing else - no [FisherStore], no
    // Fisher type in the signature. That is what lets a store-agnostic consumer target an ancillary
    // store without naming a store in its source.
    [Storage(typeof(IPlayerStore))]
    public static void Handle(RecordPlayerScore command, IDocumentSession session)
    {
        session.Store(new Player { Id = command.Name, Score = command.Score });
    }
}
