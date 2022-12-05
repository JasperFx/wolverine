using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Persistence.Durability;

public class RecoverIncomingMessagesTests
{
    public const int theRecoveryBatchSize = 100;
    public const int theBufferedLimit = 500;
    private readonly RecoverIncomingMessages theAction;

    private readonly IListeningAgent theAgent = Substitute.For<IListeningAgent, IListenerCircuit>();

    private readonly IEndpointCollection theEndpoints = Substitute.For<IEndpointCollection>();

    private readonly AdvancedSettings theSettings = new(null)
    {
        RecoveryBatchSize = theRecoveryBatchSize
    };

    public RecoverIncomingMessagesTests()
    {
        theAction = new RecoverIncomingMessages(theSettings, NullLogger.Instance, theEndpoints);

        var settings = new LocalQueueSettings("one");
        settings.BufferingLimits = new BufferingLimits(theBufferedLimit, 100);

        theAgent.Endpoint.Returns(settings);
    }

    [Theory]
    [InlineData(ListeningStatus.TooBusy)]
    [InlineData(ListeningStatus.Stopped)]
    [InlineData(ListeningStatus.Unknown)]
    public void not_accepting(ListeningStatus status)
    {
        theAgent.Status.Returns(status);
        theAction.DeterminePageSize(theAgent, new IncomingCount(TransportConstants.DurableLocalUri, 50))
            .ShouldBe(0);
    }


    [Theory]
    [InlineData("When only limited by batch size", 0, 5000, theRecoveryBatchSize)]
    [InlineData("Limited by number on server", 0, 8, 8)]
    [InlineData("Limited by number on server 2", 492, 8, 8)]
    [InlineData("Limited by queue count and buffered limit", 433, 300, 66)]
    [InlineData("Already at buffered limit", 505, 300, 0)]
    public void determine_page_size(string description, int queueLimit, int serverCount, int expected)
    {
        theAgent.QueueCount.Returns(queueLimit);
        theAgent.Status.Returns(ListeningStatus.Accepting);

        theAction.DeterminePageSize(theAgent, new IncomingCount(TransportConstants.LocalUri, serverCount))
            .ShouldBe(expected);
    }

    [Fact]
    public async Task do_nothing_when_page_size_is_0()
    {
        var action = Substitute.For<RecoverIncomingMessages>(theSettings, NullLogger.Instance, theEndpoints);
        var count = new IncomingCount(new Uri("stub://one"), 23);

        action.DeterminePageSize(theAgent, count).Returns(0);

        var persistence = Substitute.For<IMessageStore>();

        theEndpoints.FindListeningAgent(count.Destination)
            .Returns(theAgent);

        var shouldFetchMore = await action.TryRecoverIncomingMessagesAsync(persistence, count);
        shouldFetchMore.ShouldBeFalse();

        await action.DidNotReceive().RecoverMessagesAsync(persistence, count, Arg.Any<int>(), theAgent);
    }

    [Fact]
    public async Task recover_messages_when_page_size_is_non_zero_but_all_were_recovered()
    {
        var action = Substitute.For<RecoverIncomingMessages>(theSettings, NullLogger.Instance, theEndpoints);
        var count = new IncomingCount(new Uri("stub://one"), 11);

        action.DeterminePageSize(theAgent, count).Returns(11);

        var persistence = Substitute.For<IMessageStore>();

        theEndpoints.FindListeningAgent(count.Destination)
            .Returns(theAgent);

        var shouldFetchMore = await action.TryRecoverIncomingMessagesAsync(persistence, count);
        shouldFetchMore.ShouldBeFalse();

        await action.Received().RecoverMessagesAsync(persistence, count, 11, theAgent);
    }


    [Fact]
    public async Task recover_messages_when_page_size_is_non_zero_and_not_all_on_server_were_were_recovered()
    {
        var action = Substitute.For<RecoverIncomingMessages>(theSettings, NullLogger.Instance, theEndpoints);
        var count = new IncomingCount(new Uri("stub://one"), 100);

        action.DeterminePageSize(theAgent, count).Returns(11);

        var persistence = Substitute.For<IMessageStore>();

        theEndpoints.FindListeningAgent(count.Destination)
            .Returns(theAgent);

        var shouldFetchMore = await action.TryRecoverIncomingMessagesAsync(persistence, count);
        shouldFetchMore.ShouldBeTrue();

        await action.Received().RecoverMessagesAsync(persistence, count, 11, theAgent);
    }
}