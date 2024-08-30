using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Xunit;

namespace PersistenceTests.Durability;

public class CheckRecoverableIncomingMessageOperationTests
{
    public const int theRecoveryBatchSize = 100;
    public const int theBufferedLimit = 500;
    private readonly RecoverIncomingMessagesCommand theAction;

    private readonly IListeningAgent theAgent = Substitute.For<IListeningAgent, IListenerCircuit>();

    private readonly IEndpointCollection theEndpoints = Substitute.For<IEndpointCollection>();

    private readonly DurabilitySettings theSettings = new()
    {
        RecoveryBatchSize = theRecoveryBatchSize
    };

    public CheckRecoverableIncomingMessageOperationTests()
    {
        theAction = new RecoverIncomingMessagesCommand(Substitute.For<IMessageDatabase>(), new IncomingCount(new Uri("local://one"), 4), theAgent, theSettings, NullLogger.Instance);

        var settings = new LocalQueue("one");
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
        theAction.DeterminePageSize(theAgent, new IncomingCount(TransportConstants.DurableLocalUri, 50), theSettings)
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

        theAction.DeterminePageSize(theAgent, new IncomingCount(TransportConstants.LocalUri, serverCount), theSettings)
            .ShouldBe(expected);
    }
}