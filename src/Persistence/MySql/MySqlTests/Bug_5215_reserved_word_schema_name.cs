using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.MySql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace MySqlTests;

public class Bug_5215_reserved_word_schema_name
{
    [Theory]
    [InlineData("order")]
    [InlineData("user")]
    public async Task message_storage_works_in_a_schema_named_after_a_reserved_word(string schemaName)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.Durability.DurabilityAgentEnabled = false;

                opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, schemaName);
                opts.Policies.UseDurableLocalQueues();

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync(TestContext.Current.CancellationToken);

        var tracked = await host.TrackActivity().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new ReservedSchemaMessage("Tom Landry"));

        tracked.Executed.SingleMessage<ReservedSchemaMessage>().Name.ShouldBe("Tom Landry");

        var runtime = host.GetRuntime();
        var database = (IMessageDatabase)runtime.Storage;

        // The sweep logs and swallows SQL errors, so assert that the orphan was actually released
        var orphanId = await insertIncomingOwnedByAsync(schemaName, 99);
        await new ReleaseOrphanedMessagesCommand(database, runtime.DurabilitySettings, NullLogger.Instance, [1], 0)
            .ExecuteAsync(runtime, TestContext.Current.CancellationToken);
        (await ownerOfAsync(schemaName, orphanId)).ShouldBe(0);

        await new DeleteExpiredHandledEnvelopesCommand(database, runtime.DurabilitySettings, NullLogger.Instance)
            .ExecuteAsync(runtime, TestContext.Current.CancellationToken);

        var agent = new DurabilityAgent(runtime, database);
        await new DatabaseOperationBatch(database, agent.buildOperationBatch())
            .ExecuteAsync(runtime, TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> insertIncomingOwnedByAsync(string schemaName, int ownerId)
    {
        var id = Guid.NewGuid();
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();
        await conn.CreateCommand(
                $"insert into `{schemaName}`.wolverine_incoming_envelopes (id, status, owner_id, body, message_type, received_at) values (@id, 'Incoming', @owner, @body, 'test', 'local://one')")
            .With("id", id)
            .With("owner", ownerId)
            .With("body", new byte[] { 1, 2, 3 })
            .ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<int> ownerOfAsync(string schemaName, Guid id)
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();
        return Convert.ToInt32(await conn.CreateCommand(
                $"select owner_id from `{schemaName}`.wolverine_incoming_envelopes where id = @id")
            .With("id", id)
            .ExecuteScalarAsync());
    }
}

public record ReservedSchemaMessage(string Name);

public static class ReservedSchemaMessageHandler
{
    public static void Handle(ReservedSchemaMessage message)
    {
    }
}
