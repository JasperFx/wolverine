using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace PostgresqlTests.Bugs;

// GH-4151: an envelope whose executor cannot be built died in HandlerPipeline's last line of defense, which
// acks the message out of the way. On a durable transport that meant the inbox row was marked Handled with
// attempts=0 and then removed by ordinary durable-inbox cleanup -- byte for byte the lifecycle of a message
// that was handled *successfully*. Nothing in the message store distinguished "never handled, executor could
// not be built" from "handled", the dead letter table stayed empty, and the host stayed healthy. The
// reporter lost every message of one type in production this way.
//
// So this asserts against the message store itself rather than a tracked session: the row that used to be
// absent has to be there.
public class Bug_4151_executor_failure_dead_letters_the_envelope : IAsyncLifetime
{
    private const string SchemaName = "gh4151";

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);

                // Two sticky handlers for Gh4151Message and no unsticky one, so HandlerFor(type, endpoint)
                // has nothing to hand back for the queue the message actually arrives on and throws while
                // the executor is being built -- before any HandlerChain exists to carry a failure policy.
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(Gh4151GreenHandler))
                    .IncludeType(typeof(Gh4151BlueHandler));

                opts.LocalQueue("gh4151-durable").UseDurableInbox();

                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();

        await _host.Services.GetRequiredService<IWolverineRuntime>().Storage.Admin.ClearAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_envelope_lands_in_the_dead_letter_table()
    {
        await _host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c =>
                c.EndpointFor(new Uri("local://gh4151-durable")).SendAsync(new Gh4151Message()).AsTask());

        // The evidence the reporter could not find: a durable message that was never handled is now
        // accounted for, visible, and replayable instead of silently swept.
        (await countRowsAsync("wolverine_dead_letters")).ShouldBe(1);
    }

    private static async Task<long> countRowsAsync(string tableName)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select count(*) from {SchemaName}.{tableName}";
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}

public record Gh4151Message;

[StickyHandler("gh4151-green")]
public static class Gh4151GreenHandler
{
    public static void Handle(Gh4151Message message)
    {
    }
}

[StickyHandler("gh4151-blue")]
public static class Gh4151BlueHandler
{
    public static void Handle(Gh4151Message message)
    {
    }
}
