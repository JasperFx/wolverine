using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
    public void determine_page_size(string _, int queueLimit, int serverCount, int expected)
    {
        theAgent.QueueCount.Returns(queueLimit);
        theAgent.Status.Returns(ListeningStatus.Accepting);

        theAction.DeterminePageSize(theAgent, new IncomingCount(TransportConstants.LocalUri, serverCount), theSettings)
            .ShouldBe(expected);
    }

    public static IEnumerable<object[]> CountValues()
    {
        yield return [5];
        yield return [5L];
        yield return [5m];
    }

    [Theory]
    [MemberData(nameof(CountValues))]
    /// <summary>
    /// GH-4480: Ensure that ReadResultsAsync can handle different numeric types for the count column in the database result set.
    /// Some database providers (...Oracle) return non-int types so we need to be able to handle that gracefully.
    /// </summary>
    public async Task read_results_accepts_provider_count_types_4480(object count)
    {
        var destination = new Uri("local://one");
        theAgent.Status.Returns(ListeningStatus.Accepting);
        theEndpoints.IsSingleNodeListener(destination).Returns(false);
        theEndpoints.FindListenerCircuit(destination).Returns(theAgent);

        var operation = new CheckRecoverableIncomingMessagesOperation(Substitute.For<IMessageDatabase>(), theEndpoints,
            theSettings, NullLogger.Instance);

        await operation.ReadResultsAsync(readerFor(destination, count), [], CancellationToken.None);

        operation.PostProcessingCommands().ShouldHaveSingleItem()
            .ShouldBeOfType<RecoverIncomingMessagesCommand>();
    }

    private static DbDataReader readerFor(Uri destination, object count)
    {
        var reader = Substitute.For<DbDataReader>();
        reader.ReadAsync(Arg.Any<CancellationToken>()).Returns(true, false);
        reader.GetFieldValueAsync<string>(0, Arg.Any<CancellationToken>()).Returns(destination.ToString());
        reader.GetValue(1).Returns(count);

        if (count is int intCount)
        {
            reader.GetFieldValueAsync<int>(1, Arg.Any<CancellationToken>()).Returns(intCount);
        }
        else
        {
            reader.GetFieldValueAsync<int>(1, Arg.Any<CancellationToken>())
                .Throws(new InvalidCastException("Specified cast is not valid."));
        }

        return reader;
    }
}