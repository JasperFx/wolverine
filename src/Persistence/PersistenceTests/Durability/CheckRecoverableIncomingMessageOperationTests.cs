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

    /// <summary>
    /// GH-4480: ReadResultsAsync has to handle whatever numeric type the provider surfaced for the count
    /// column. Some providers (Oracle above all) return non-int types for count(*), because an
    /// unconstrained NUMBER carries no precision for them to narrow against.
    /// </summary>
    /// <remarks>
    /// The doc comment belongs above the attributes: between them and the signature it documents no
    /// language element, which is a CS1587 rather than a doc comment.
    /// </remarks>
    [Theory]
    [MemberData(nameof(CountValues))]
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

    /// <summary>
    /// A reader standing in for a provider that surfaced <paramref name="count" />'s type for the count
    /// column, and that -- like every real ADO.NET provider -- throws <c>InvalidCastException</c> from
    /// <c>GetFieldValueAsync&lt;int&gt;()</c> when that type is anything but <c>Int32</c>.
    /// </summary>
    /// <remarks>
    /// That throwing stub is the load-bearing part. A substituted reader otherwise answers whatever the
    /// production code happens to ask it, so it agrees with the code by construction and can never catch a
    /// provider-mapping mismatch -- which is exactly why the pre-GH-4480 coverage here was green against a
    /// call that threw on Oracle. Keeping the cast wired to throw means this stays a real assertion no
    /// matter which read the production code settles on.
    /// </remarks>
    private static DbDataReader readerFor(Uri destination, object count)
    {
        var reader = Substitute.For<DbDataReader>();
        reader.ReadAsync(Arg.Any<CancellationToken>()).Returns(true, false);
        reader.GetFieldValueAsync<string>(0, Arg.Any<CancellationToken>()).Returns(destination.ToString());

        reader.IsDBNullAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        reader.GetValue(1).Returns(count);
        reader.GetFieldValueAsync<object>(1, Arg.Any<CancellationToken>()).Returns(count);

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

    /// <summary>
    /// GH-4480 follow-up. The null guard in the shared helper, which the three type cases above never reach.
    /// </summary>
    [Fact]
    public async Task a_null_count_reads_as_zero()
    {
        var reader = Substitute.For<DbDataReader>();
        reader.IsDBNullAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        (await reader.GetInt32TolerantlyAsync(1, CancellationToken.None)).ShouldBe(0);
    }
}
