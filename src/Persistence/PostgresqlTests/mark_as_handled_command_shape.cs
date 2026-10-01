using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Xunit;

namespace PostgresqlTests;

/// <summary>
/// GH-4705. Marten, Polecat and Fisher queue the mark-as-handled UPDATE into somebody else's batch, so they
/// need the SQL as text rather than a method to call. Marten's QueueSqlCommand binds POSITIONALLY, which is
/// why <see cref="MessageDatabase{T}.BuildMarkIncomingAsHandled" /> hands back the arguments in placeholder
/// order instead of leaving each caller to mirror it.
///
/// Mirroring it is not a small ask: under inbox partitioning the statement is two statements, the envelope id
/// appears three times, and the destination appears two or three times depending on MessageIdentity. These
/// assert the two stay in step across every combination -- an off-by-one here is a silent wrong-row update,
/// or an Npgsql bind error at commit time on a path only durable-inbox traffic reaches.
/// </summary>
public class mark_as_handled_command_shape : PostgresqlContext
{
    private static PostgresqlMessageStore storeFor(MessageIdentity identity, bool partitioned)
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.PostgresConnectionString,
            SchemaName = "mark_handled_shape"
        };

        var durability = new DurabilitySettings
        {
            MessageIdentity = identity,
            EnableInboxPartitioning = partitioned
        };

        return new PostgresqlMessageStore(settings, durability,
            NpgsqlDataSource.Create(Servers.PostgresConnectionString),
            NullLogger<PostgresqlMessageStore>.Instance);
    }

    private static Envelope theEnvelope => new()
    {
        Id = Guid.NewGuid(),
        Destination = new Uri("rabbitmq://queue/incoming")
    };

    [Theory]
    [InlineData(MessageIdentity.IdOnly, false)]
    [InlineData(MessageIdentity.IdOnly, true)]
    [InlineData(MessageIdentity.IdAndDestination, false)]
    [InlineData(MessageIdentity.IdAndDestination, true)]
    public async Task positional_arguments_match_the_placeholders_one_for_one(MessageIdentity identity, bool partitioned)
    {
        await using var store = storeFor(identity, partitioned);
        var envelope = theEnvelope;
        var keepUntil = DateTimeOffset.UtcNow.AddMinutes(5);

        var command = store.BuildMarkIncomingAsHandled(envelope, keepUntil, "?", "?", "?");

        command.Arguments.Length.ShouldBe(command.Sql.Count(c => c == '?'));
    }

    [Theory]
    [InlineData(MessageIdentity.IdOnly, false)]
    [InlineData(MessageIdentity.IdOnly, true)]
    [InlineData(MessageIdentity.IdAndDestination, false)]
    [InlineData(MessageIdentity.IdAndDestination, true)]
    public async Task positional_arguments_are_in_placeholder_order(MessageIdentity identity, bool partitioned)
    {
        await using var store = storeFor(identity, partitioned);
        var envelope = theEnvelope;
        var keepUntil = DateTimeOffset.UtcNow.AddMinutes(5);

        // Name each placeholder after the column it sits against, by rebuilding the statement with distinct
        // markers and reading them off in order. Counting alone would pass on a command that binds the right
        // number of values in the wrong places.
        var named = store.BuildMarkIncomingAsHandled(envelope, keepUntil, ":ID", ":URI", ":KEEP").Sql;
        var expected = named.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1) // everything before the first marker
            .Select(x => new string(x.TakeWhile(char.IsUpper).ToArray()))
            .ToArray();

        var actual = store.BuildMarkIncomingAsHandled(envelope, keepUntil, "?", "?", "?").Arguments
            .Select(x => x switch
            {
                Guid => "ID",
                string => "URI",
                DateTimeOffset => "KEEP",
                _ => "?"
            })
            .ToArray();

        actual.ShouldBe(expected);
    }

    [Theory]
    [InlineData(MessageIdentity.IdOnly, false)]
    [InlineData(MessageIdentity.IdOnly, true)]
    [InlineData(MessageIdentity.IdAndDestination, false)]
    [InlineData(MessageIdentity.IdAndDestination, true)]
    public async Task each_statement_can_be_queued_on_its_own(MessageIdentity identity, bool partitioned)
    {
        await using var store = storeFor(identity, partitioned);
        var envelope = theEnvelope;
        var keepUntil = DateTimeOffset.UtcNow.AddMinutes(5);

        var command = store.BuildMarkIncomingAsHandled(envelope, keepUntil, "?", "?", "?");

        // Marten's QueueSqlCommand throws on a ';', so under partitioning the delete and the update have to be
        // queued one at a time -- each with exactly the arguments its own placeholders bind
        command.Statements.Length.ShouldBe(partitioned ? 2 : 1);
        foreach (var statement in command.Statements)
        {
            statement.Sql.ShouldNotContain(';');
            statement.Arguments.Length.ShouldBe(statement.Sql.Count(c => c == '?'));
        }

        // ... and together they are the very statement the store executes itself
        string.Join(";", command.Statements.Select(x => x.Sql)).ShouldBe(command.Sql);
        command.Statements.SelectMany(x => x.Arguments).ShouldBe(command.Arguments);
    }

    [Theory]
    [InlineData(MessageIdentity.IdOnly)]
    [InlineData(MessageIdentity.IdAndDestination)]
    public async Task the_statement_always_matches_the_whole_inbox_identity(MessageIdentity identity)
    {
        await using var store = storeFor(identity, false);

        // THE GH-4705 defect: the three stores each wrote "... where id = ?" with no received_at predicate,
        // so the first handler to commit retired every destination's copy of a fanned-out message.
        store.BuildMarkIncomingAsHandled(theEnvelope, DateTimeOffset.UtcNow, "@id", "@uri", "@keepUntil").Sql
            .ShouldContain($"{DatabaseConstants.ReceivedAt} = @uri");
    }

    [Fact]
    public async Task partitioning_brings_the_delete_first_shape_along()
    {
        await using var store = storeFor(MessageIdentity.IdAndDestination, true);

        // The other half of GH-4701: with a retained Handled row for the same identity, flipping status is a
        // cross-partition move onto a key that already exists, so the row could not be retired at all. These
        // stores skipped that shape entirely -- on Marten, whose store is the one provider that has inbox
        // partitioning in the first place.
        store.BuildMarkIncomingAsHandled(theEnvelope, DateTimeOffset.UtcNow, "@id", "@uri", "@keepUntil").Sql
            .ShouldStartWith("delete from");
    }
}
