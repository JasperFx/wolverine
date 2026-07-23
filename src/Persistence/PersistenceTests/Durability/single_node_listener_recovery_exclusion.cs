using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Xunit;

namespace PersistenceTests.Durability;

/// <summary>
/// GH-3590. The per-database durability agent is distributed independently of the listener agents, so it will
/// routinely run on a node that is NOT hosting a given exclusive (or leader-pinned) listener. It must never
/// claim inbox rows for those endpoints — the listening node recovers them itself through
/// <see cref="ListenerInboxRecovery"/>.
/// </summary>
public class single_node_listener_recovery_exclusion
{
    private readonly IEndpointCollection theEndpoints = Substitute.For<IEndpointCollection>();
    private readonly IMessageDatabase theDatabase = Substitute.For<IMessageDatabase>();

    private readonly DurabilitySettings theSettings = new()
    {
        RecoveryBatchSize = 100
    };

    private readonly Uri theExclusiveUri = new("rabbitmq://queue/exclusive");
    private readonly Uri theCompetingUri = new("rabbitmq://queue/competing");

    private async Task<IAgentCommand[]> commandsFor(params Uri[] destinations)
    {
        var operation =
            new CheckRecoverableIncomingMessagesOperation(theDatabase, theEndpoints, theSettings,
                NullLogger.Instance);

        var reader = Substitute.For<DbDataReader>();
        var index = -1;
        reader.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            index++;
            return index < destinations.Length;
        });
        reader.GetFieldValueAsync<string>(0, Arg.Any<CancellationToken>())
            .Returns(_ => destinations[index].ToString());
        reader.GetFieldValueAsync<int>(1, Arg.Any<CancellationToken>()).Returns(_ => 5);

        await operation.ReadResultsAsync(reader, new List<Exception>(), CancellationToken.None);

        return operation.PostProcessingCommands().ToArray();
    }

    private IListenerCircuit acceptingCircuitFor(Uri uri, ListenerScope scope)
    {
        var endpoint = new LocalQueue(uri.Segments.Last())
        {
            ListenerScope = scope,
            BufferingLimits = new BufferingLimits(500, 100)
        };

        var circuit = Substitute.For<IListeningAgent, IListenerCircuit>();
        circuit.Endpoint.Returns(endpoint);
        circuit.Status.Returns(ListeningStatus.Accepting);
        circuit.QueueCount.Returns(0);

        theEndpoints.FindListenerCircuit(uri).Returns(circuit);

        return circuit;
    }

    [Fact]
    public async Task does_not_issue_a_recovery_command_for_an_exclusive_listener()
    {
        acceptingCircuitFor(theExclusiveUri, ListenerScope.Exclusive);
        theEndpoints.IsSingleNodeListener(theExclusiveUri).Returns(true);

        var commands = await commandsFor(theExclusiveUri);

        commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task never_even_looks_up_a_circuit_for_a_single_node_listener()
    {
        // The FindListenerCircuit() fallback resolves an unknown address to the durable local queue, which
        // would happily swallow another node's messages. The skip has to happen first.
        theEndpoints.IsSingleNodeListener(theExclusiveUri).Returns(true);

        await commandsFor(theExclusiveUri);

        theEndpoints.DidNotReceive().FindListenerCircuit(theExclusiveUri);
    }

    [Fact]
    public async Task still_recovers_for_a_competing_consumers_listener()
    {
        acceptingCircuitFor(theCompetingUri, ListenerScope.CompetingConsumers);
        theEndpoints.IsSingleNodeListener(theCompetingUri).Returns(false);

        var commands = await commandsFor(theCompetingUri);

        commands.Single().ShouldBeOfType<RecoverIncomingMessagesCommand>();
    }

    [Fact]
    public async Task skips_only_the_single_node_destination_in_a_mixed_batch()
    {
        acceptingCircuitFor(theExclusiveUri, ListenerScope.Exclusive);
        acceptingCircuitFor(theCompetingUri, ListenerScope.CompetingConsumers);

        theEndpoints.IsSingleNodeListener(theExclusiveUri).Returns(true);
        theEndpoints.IsSingleNodeListener(theCompetingUri).Returns(false);

        var commands = await commandsFor(theExclusiveUri, theCompetingUri);

        commands.Single().ShouldBeOfType<RecoverIncomingMessagesCommand>();
    }

    [Theory]
    [InlineData(ListenerScope.Exclusive)]
    [InlineData(ListenerScope.PinnedToLeader)]
    public void determine_page_size_is_zero_for_a_single_node_listener(ListenerScope scope)
    {
        var circuit = acceptingCircuitFor(theExclusiveUri, scope);

        var command = new RecoverIncomingMessagesCommand(theDatabase, new IncomingCount(theExclusiveUri, 50),
            circuit, theSettings, NullLogger.Instance);

        command.DeterminePageSize(circuit, new IncomingCount(theExclusiveUri, 50), theSettings).ShouldBe(0);
    }

    [Fact]
    public void determine_page_size_is_unchanged_for_competing_consumers()
    {
        var circuit = acceptingCircuitFor(theCompetingUri, ListenerScope.CompetingConsumers);

        var command = new RecoverIncomingMessagesCommand(theDatabase, new IncomingCount(theCompetingUri, 50),
            circuit, theSettings, NullLogger.Instance);

        command.DeterminePageSize(circuit, new IncomingCount(theCompetingUri, 50), theSettings).ShouldBe(50);
    }
}
