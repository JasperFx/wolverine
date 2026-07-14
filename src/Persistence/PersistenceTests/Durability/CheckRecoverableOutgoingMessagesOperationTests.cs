using System.Data.Common;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;
using Xunit;

namespace PersistenceTests.Durability;

public class CheckRecoverableOutgoingMessagesOperationTests
{
    private static readonly Uri TheUnknownDestination = "rabbitmq://queue/items".ToUri();
    private static readonly Uri TheKnownDestination = "stub://one".ToUri();

    private readonly IEndpointCollection theEndpoints = Substitute.For<IEndpointCollection>();
    private readonly IWolverineRuntime theRuntime = Substitute.For<IWolverineRuntime>();
    private readonly CheckRecoverableOutgoingMessagesOperation theOperation;

    public CheckRecoverableOutgoingMessagesOperationTests()
    {
        theRuntime.Endpoints.Returns(theEndpoints);

        theOperation = new CheckRecoverableOutgoingMessagesOperation(Substitute.For<IMessageDatabase>(), theRuntime,
            NullLogger.Instance);
    }

    /// <summary>
    /// A leftover outbox row for a transport this node does not have registered used to abort
    /// the recovery of every *other* destination in the same sweep.
    /// See https://github.com/JasperFx/wolverine/issues/3413.
    /// </summary>
    [Fact]
    public async Task keep_recovering_other_destinations_when_one_transport_cannot_be_resolved()
    {
        theEndpoints.GetOrBuildSendingAgent(TheUnknownDestination)
            .Throws(new UnknownTransportException(
                $"There is no known transport type that can send to the Destination {TheUnknownDestination}"));

        var knownAgent = Substitute.For<ISendingAgent>();
        knownAgent.Latched.Returns(false);
        knownAgent.Destination.Returns(TheKnownDestination);
        theEndpoints.GetOrBuildSendingAgent(TheKnownDestination).Returns(knownAgent);

        await theOperation.ReadResultsAsync(readerFor(TheUnknownDestination, TheKnownDestination), [],
            CancellationToken.None);

        var commands = theOperation.PostProcessingCommands().ToArray();

        commands.ShouldHaveSingleItem()
            .ShouldBeOfType<RecoverOutgoingMessagesCommand>();
    }

    private static DbDataReader readerFor(params Uri[] destinations)
    {
        var reader = Substitute.For<DbDataReader>();

        var reads = destinations.Select(_ => true).Append(false).ToArray();
        reader.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(reads[0], reads.Skip(1).ToArray());

        var raw = destinations.Select(x => x.ToString()).ToArray();
        reader.GetFieldValueAsync<string>(0, Arg.Any<CancellationToken>())
            .Returns(raw[0], raw.Skip(1).ToArray());

        return reader;
    }
}
