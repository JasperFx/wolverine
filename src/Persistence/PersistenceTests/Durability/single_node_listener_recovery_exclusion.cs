using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests.ExclusiveListeners;
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
        // Deliberately NOT a LocalQueue: a local queue is never a single node listener no matter what its
        // ListenerScope says, which is the whole point of GH-3856 below.
        var endpoint = new SingleNodeListenerEndpoint(uri.Segments.Last())
        {
            ListenerScope = scope,
            BufferingLimits = new BufferingLimits(500, 100)
        };

        return acceptingCircuitFor(uri, endpoint);
    }

    private IListenerCircuit acceptingCircuitFor(Uri uri, Endpoint endpoint)
    {
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

    private readonly Uri theLocalUri = new("local://activiteiten3");

    private LocalQueue exclusiveLocalQueue()
    {
        // Exactly what PartitionedMessageTopology produces for PublishToPartitionedLocalMessaging(): a durable
        // local queue forced to ListenerScope.Exclusive.
        return new LocalQueue("activiteiten3")
        {
            ListenerScope = ListenerScope.Exclusive,
            Mode = EndpointMode.Durable,
            BufferingLimits = new BufferingLimits(500, 100)
        };
    }

    /// <summary>
    /// GH-3856. A local queue never gets a ListeningAgent -- LocalQueue.BuildListenerAsync() throws and
    /// StartListenersAsync() filters local queues out -- so nothing ever starts the ListenerInboxRecoveryLoop
    /// that the GH-3590 carve-out hands ownership to. If the durability agent skips it too, its dormant inbox
    /// rows are recovered by nobody and sit at owner_id = 0 forever.
    /// </summary>
    [Fact]
    public void a_local_queue_is_never_a_single_node_listener_whatever_its_scope()
    {
        exclusiveLocalQueue().IsSingleNodeListener.ShouldBeFalse();

        new LocalQueue("pinned") { ListenerScope = ListenerScope.PinnedToLeader }
            .IsSingleNodeListener.ShouldBeFalse();
    }

    [Fact]
    public async Task still_recovers_for_an_exclusive_local_queue()
    {
        acceptingCircuitFor(theLocalUri, exclusiveLocalQueue());
        theEndpoints.IsSingleNodeListener(theLocalUri).Returns(false);

        var commands = await commandsFor(theLocalUri);

        commands.Single().ShouldBeOfType<RecoverIncomingMessagesCommand>();
    }

    /// <summary>
    /// GH-3856. The second, independent guard. Getting past the CheckRecoverableIncomingMessagesOperation skip
    /// is not enough on its own -- DeterminePageSize() used to test the raw ListenerScope, so an exclusive local
    /// queue got a command issued and then recovered zero rows on every single pass.
    /// </summary>
    [Fact]
    public void determine_page_size_is_unchanged_for_an_exclusive_local_queue()
    {
        var circuit = acceptingCircuitFor(theLocalUri, exclusiveLocalQueue());

        var command = new RecoverIncomingMessagesCommand(theDatabase, new IncomingCount(theLocalUri, 50),
            circuit, theSettings, NullLogger.Instance);

        command.DeterminePageSize(circuit, new IncomingCount(theLocalUri, 50), theSettings).ShouldBe(50);
    }
}
