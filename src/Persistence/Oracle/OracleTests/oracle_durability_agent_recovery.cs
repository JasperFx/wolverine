using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Oracle;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace OracleTests;

/// <summary>
///     GH-3614, end to end. The durability agent batches its whole recovery operation set into one
///     command builder and executes it. On Oracle that batch used to arrive as a single command full
///     of semicolon-separated statements bound with <c>@</c> markers, which ODP.NET rejected outright
///     with ORA-00933 / ORA-00936 -- so the agent threw on every sweep and nothing persisted in the
///     inbox or outbox was ever recovered.
///     <para>
///     This runs the real <see cref="DurabilityAgent.buildOperationBatch" /> set against a real Oracle
///     database, which is the assertion that actually would have caught the bug.
///     </para>
/// </summary>
[Collection("oracle")]
public class oracle_durability_agent_recovery : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE");

                // Balanced rather than Solo -- ReleaseOrphanedMessagesOperation is only part of the
                // recovery batch outside Solo mode, and it is one of the two-statement operations
                opts.Durability.Mode = DurabilityMode.Balanced;
            })
            .StartAsync();

        await theHost.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private (IWolverineRuntime, IMessageDatabase) theRuntimeAndDatabase()
    {
        var runtime = theHost.Services.GetRequiredService<IWolverineRuntime>();
        return (runtime, (IMessageDatabase)runtime.Storage);
    }

    [Fact]
    public async Task the_full_recovery_batch_executes_against_oracle()
    {
        var (runtime, database) = theRuntimeAndDatabase();

        var operations = new DurabilityAgent(runtime, database).buildOperationBatch();
        operations.ShouldNotBeEmpty();

        // Before the fix this threw DatabaseBatchCommandException wrapping ORA-00933 / ORA-00936
        await new DatabaseOperationBatch(database, operations).ExecuteAsync(runtime, CancellationToken.None);
    }

    [Fact]
    public async Task the_recovery_batch_releases_messages_owned_by_a_dead_node()
    {
        var (runtime, database) = theRuntimeAndDatabase();

        // Persist an incoming envelope owned by a node number that does not exist any more --
        // exactly the state ReleaseOrphanedMessagesOperation is there to clean up
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.OwnerId = 8888;

        await database.Inbox.StoreIncomingAsync(envelope);
        (await ownerOf(envelope.Id)).ShouldBe(8888);

        await new DatabaseOperationBatch(database, new DurabilityAgent(runtime, database).buildOperationBatch())
            .ExecuteAsync(runtime, CancellationToken.None);

        (await ownerOf(envelope.Id)).ShouldBe(TransportConstants.AnyNode);
    }

    [Fact]
    public async Task replayable_dead_letters_are_moved_back_to_the_inbox()
    {
        var (runtime, database) = theRuntimeAndDatabase();

        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;

        await database.Inbox.StoreIncomingAsync(envelope);
        await database.Inbox.MoveToDeadLetterStorageAsync(envelope, new DivideByZeroException("boom"));
        await database.DeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync([envelope.Id]);

        // This is the operation that writes two statements -- the insert and the delete -- so it is
        // the one that proves per-statement splitting works, not just per-operation
        await new DatabaseOperationBatch(database, new DurabilityAgent(runtime, database).buildOperationBatch())
            .ExecuteAsync(runtime, CancellationToken.None);

        (await countAsync(
                $"select count(*) from WOLVERINE.{DatabaseConstants.IncomingTable} where id = :id", envelope.Id))
            .ShouldBe(1);
        (await countAsync(
                $"select count(*) from WOLVERINE.{DatabaseConstants.DeadLetterTable} where id = :id", envelope.Id))
            .ShouldBe(0);
    }

    private async Task<int> ownerOf(Guid id)
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync();

        await using var command = conn.CreateCommand();
        command.BindByName = true;
        command.CommandText =
            $"select owner_id from WOLVERINE.{DatabaseConstants.IncomingTable} where id = :id";
        command.Parameters.Add(new OracleParameter("id", OracleDbType.Raw) { Value = id.ToByteArray() });

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> countAsync(string sql, Guid id)
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync();

        await using var command = conn.CreateCommand();
        command.BindByName = true;
        command.CommandText = sql;
        command.Parameters.Add(new OracleParameter("id", OracleDbType.Raw) { Value = id.ToByteArray() });

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
