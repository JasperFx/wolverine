using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.RDBMS;

namespace PostgresqlTests.Transport;

/// <summary>
/// https://github.com/JasperFx/wolverine/issues/5105. A durable listener's dead-letter move only posted the write to
/// a background retry block; the envelope was then completed -- its inbox row, the only copy, marked handled -- and
/// reported as moved to the error queue. A write that kept failing was discarded, and the message was gone with
/// every log line and metric saying it had been dead lettered.
/// </summary>
public class Bug_5105_failed_dead_letter_write
{
    private const string SchemaName = "gh5105";

    [Fact]
    public async Task a_message_that_could_not_be_dead_lettered_is_dead_lettered_once_the_store_returns()
    {
        Gh5105PoisonHandler.Attempts = 0;

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await conn.DropSchemaAsync(SchemaName, ct: TestContext.Current.CancellationToken);

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Gh5105PoisonHandler));
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, SchemaName,
                    transportSchema: SchemaName).AutoProvision();

                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.ListenToPostgresqlQueue("gh5105").UseDurableInbox();
                opts.PublishMessage<Gh5105Poison>().ToPostgresqlQueue("gh5105");

                opts.OnException<DivideByZeroException>().MoveToErrorQueue();
            }).StartAsync(TestContext.Current.CancellationToken);

        // The dead-letter store is unreachable
        await execute(conn, $"alter table {SchemaName}.{DatabaseConstants.DeadLetterTable} rename to gh5105_offline");

        await host.MessageBus().SendAsync(new Gh5105Poison());

        // Long enough for the handler to fail and the dead-letter write to give up
        await waitForAsync(() => Task.FromResult(Gh5105PoisonHandler.Attempts > 0), 30.Seconds());
        await Task.Delay(6.Seconds(), TestContext.Current.CancellationToken);

        // The store comes back
        await execute(conn, $"alter table {SchemaName}.gh5105_offline rename to {DatabaseConstants.DeadLetterTable}");

        // Before GH-5105 the inbox row had been marked handled and nothing was ever written here
        var deadLettered = await waitForAsync(async () =>
        {
            var count = await conn
                .CreateCommand($"select count(*) from {SchemaName}.{DatabaseConstants.DeadLetterTable}")
                .ExecuteScalarAsync(TestContext.Current.CancellationToken);
            return Convert.ToInt32(count) == 1;
        }, 90.Seconds());

        deadLettered.ShouldBeTrue("the message was lost: it was never written to dead letter storage");

        // It was executed again rather than dropped
        Gh5105PoisonHandler.Attempts.ShouldBeGreaterThan(1);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static async Task execute(NpgsqlConnection conn, string sql)
    {
        await conn.CreateCommand(sql).ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<bool> waitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);
        }

        return await condition();
    }
}

public record Gh5105Poison;

public static class Gh5105PoisonHandler
{
    public static int Attempts;

    public static void Handle(Gh5105Poison message)
    {
        Interlocked.Increment(ref Attempts);
        throw new DivideByZeroException("always fails");
    }
}
