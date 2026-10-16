using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Tracking;

namespace PostgresqlTests;

/// <summary>
/// GH-5016. A scheduled cascade from a transactional handler to an endpoint that schedules natively (a PostgreSQL
/// queue) is parked in the inbox under that endpoint's address. In the other modes the scheduled job forwards such
/// a row to the endpoint when it comes due. The ServerlessWithOutbox durability pass executed it locally instead,
/// and with no handler for it in this application the message was lost
/// </summary>
public class serverless_with_outbox_native_scheduled_release_5016 : IAsyncDisposable
{
    private const string SchemaName = "serverless_native_release";
    private const string QueueName = "serverless_native_q";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost? theFunction;

    public async ValueTask DisposeAsync()
    {
        if (theFunction != null)
        {
            await theFunction.StopAsync();
            theFunction.Dispose();
        }
    }

    private static async Task<long> countAsync(string table)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);
        return Convert.ToInt64(await conn.CreateCommand($"select count(*) from {SchemaName}.{table}")
            .ExecuteScalarAsync(Token));
    }

    [Fact]
    public async Task a_due_scheduled_cascade_is_released_to_its_queue_by_a_pass()
    {
        theFunction = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(NativeScheduledReleaseHandler));
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, SchemaName,
                        transportSchema: SchemaName)
                    .AutoProvision().AutoPurgeOnStartup();
                opts.PublishMessage<NativeScheduledReleased>().ToPostgresqlQueue(QueueName);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        await theFunction.ResetResourceState(Token);

        await theFunction.MessageBus().InvokeAsync(new NativeScheduledReleaseCommand(Guid.NewGuid()), Token);

        // Parked in the inbox until it is due
        (await countAsync(DatabaseConstants.IncomingTable)).ShouldBe(1);
        (await countAsync($"wolverine_queue_{QueueName}")).ShouldBe(0);

        await Task.Delay(1500, Token);
        var report = await theFunction.RunDurabilityPassAsync(token: Token);

        report.ScheduledReleased.ShouldBe(1);

        // Sent to the queue, for whatever listens to it, and nothing left behind
        (await countAsync($"wolverine_queue_{QueueName}")).ShouldBe(1);
        (await countAsync(DatabaseConstants.IncomingTable)).ShouldBe(0);
        (await countAsync(DatabaseConstants.OutgoingTable)).ShouldBe(0);
    }
}

public record NativeScheduledReleaseCommand(Guid Id);

public record NativeScheduledReleased(Guid Id);

public static class NativeScheduledReleaseHandler
{
    [Transactional]
    public static object Handle(NativeScheduledReleaseCommand command, NpgsqlConnection connection)
        => new NativeScheduledReleased(command.Id).DelayedFor(1.Seconds());
}
