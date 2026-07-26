using System.Data.Common;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.Oracle;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.RDBMS.Polling;

namespace OracleTests;

/// <summary>
///     GH-3614. The durability agent batches several <see cref="IDatabaseOperation" />s into one
///     command builder. The generic builder emits <c>@</c> bind markers and concatenates every
///     statement into a single command, and ODP.NET rejects both -- with ORA-00933 / ORA-00936 --
///     so no persisted inbox or outbox message was ever recovered on Oracle.
///     <para>
///     Oracle now hands back a <c>Weasel.Oracle.OracleDbCommandBuilder</c>, which emits <c>:</c>
///     markers, types parameters through OracleProvider, and splits at each statement boundary.
///     These assertions run against the real durability operations, and need no database.
///     </para>
/// </summary>
public class oracle_durability_command_translation
{
    private static OracleMessageStore theStore()
    {
        return new OracleMessageStore(
            new DatabaseSettings { SchemaName = "WOLVERINE", Role = MessageStoreRole.Main },
            new DurabilitySettings(),
            new OracleDataSource(Servers.OracleConnectionString),
            NullLogger<OracleMessageStore>.Instance);
    }

    private static IReadOnlyList<DbCommand> compile(params IDatabaseOperation[] operations)
    {
        var builder = theStore().ToCommandBuilder();

        foreach (var operation in operations)
        {
            builder.StartNewCommand();
            operation.ConfigureCommand(builder);
        }

        return builder.CompileCommands();
    }

    [Fact]
    public void the_message_store_hands_back_an_oracle_shaped_builder()
    {
        theStore().ToCommandBuilder().ShouldBeOfType<Weasel.Oracle.OracleDbCommandBuilder>();
    }

    [Fact]
    public void uses_oracle_bind_markers_rather_than_the_generic_ones()
    {
        var commands = compile(
            new DeleteExpiredEnvelopesOperation(new DbObjectName("WOLVERINE", "wolverine_incoming_envelopes"),
                DateTimeOffset.UtcNow));

        var sql = commands.Single().CommandText;

        sql.ShouldContain(":p0");
        sql.ShouldNotContain("@");
    }

    [Fact]
    public void separates_batched_operations_into_individual_oracle_statements()
    {
        var store = theStore();

        var commands = compile(
            new DeleteExpiredEnvelopesOperation(new DbObjectName("WOLVERINE", "wolverine_incoming_envelopes"),
                DateTimeOffset.UtcNow),
            new MoveReplayableErrorMessagesToIncomingOperation(store),
            new DeleteOldNodeEventRecords(store, new DurabilitySettings()));

        // One command per *statement*, not per operation -- ODP.NET cannot execute several statements
        // from one command, and MoveReplayableErrorMessagesToIncomingOperation writes two (the insert
        // and the delete)
        commands.Count.ShouldBe(4);

        foreach (var command in commands)
        {
            command.ShouldBeOfType<OracleCommand>().BindByName.ShouldBeTrue();
            command.CommandText.ShouldNotContain(";");
        }
    }

    [Fact]
    public void each_statement_only_carries_its_own_parameters()
    {
        var commands = compile(
            new DeleteExpiredEnvelopesOperation(new DbObjectName("WOLVERINE", "wolverine_incoming_envelopes"),
                DateTimeOffset.UtcNow),
            new DeleteExpiredDeadLetterMessagesOperation(theStore(), NullLogger.Instance, DateTimeOffset.UtcNow));

        commands.Count.ShouldBe(2);

        foreach (var command in commands)
        {
            foreach (DbParameter parameter in command.Parameters)
            {
                command.CommandText.ShouldContain(":" + parameter.ParameterName);
            }
        }
    }

    [Fact]
    public void converts_boolean_parameters_to_an_oracle_number()
    {
        var commands = compile(new MoveReplayableErrorMessagesToIncomingOperation(theStore()));

        // The insert and the delete both bind :replayable, and it is only ever created once, so
        // both split commands have to carry it
        commands.Count.ShouldBe(2);

        foreach (var command in commands)
        {
            var parameter = command.Parameters
                .Cast<OracleParameter>()
                .Single(x => x.ParameterName == "replayable");

            parameter.OracleDbType.ShouldBe(OracleDbType.Int16);
            parameter.Value.ShouldBe(1);
        }
    }

    [Fact]
    public void converts_guid_parameters_to_oracle_raw()
    {
        var id = Guid.NewGuid();
        var builder = theStore().ToCommandBuilder();

        builder.Append("select 1 from dual where id = ");
        builder.AppendParameter(id);

        var parameter = (OracleParameter)builder.CompileCommands().Single().Parameters[0];

        parameter.OracleDbType.ShouldBe(OracleDbType.Raw);
        parameter.Value.ShouldBe(id.ToByteArray());
    }

    [Fact]
    public void date_time_offsets_keep_their_oracle_type()
    {
        var commands = compile(
            new DeleteExpiredEnvelopesOperation(new DbObjectName("WOLVERINE", "wolverine_incoming_envelopes"),
                DateTimeOffset.UtcNow));

        commands.Single().Parameters
            .Cast<OracleParameter>()
            .Single().OracleDbType.ShouldBe(OracleDbType.TimeStampTZ);
    }
}
