using CoreTests.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Persistence.Durability;

/// <summary>
/// GH-5050. When the outgoing recovery step fails to send a row, it restarts the row's stale clock, so the row
/// moves to the back of the stale order and waits OutboxStaleTime before a pass tries it again
/// </summary>
public class recover_stale_outgoing_restarts_failed_rows_5050
{
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly IStaleOutgoingSource theSource = Substitute.For<IStaleOutgoingSource>();
    private static readonly Uri Down = new("nowhere://down");

    public recover_stale_outgoing_restarts_failed_rows_5050()
    {
        // Nothing can be sent there: there is no sending agent for the destination
        theRuntime.Endpoints.GetOrBuildSendingAgent(Down)
            .Returns(_ => throw new InvalidOperationException("No transport"));
    }

    private static Envelope unsendable()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = Down;
        envelope.DeliverBy = null;
        envelope.Sender = null;
        return envelope;
    }

    private Task<DurabilityPassStepResult> executeAsync(RecoverStaleOutgoingStep step, int batchSize)
    {
        var context = new DurabilityPassContext(theRuntime, theRuntime.Storage, batchSize,
            DateTimeOffset.UtcNow.AddMinutes(1), () => DateTimeOffset.UtcNow);
        return step.ExecuteAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task restarts_the_stale_clock_of_the_rows_that_failed_to_send()
    {
        theRuntime.Options.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
        var failed = new[] { unsendable(), unsendable() };
        theSource.LoadStaleOutgoingAsync(default, default!, default, TestContext.Current.CancellationToken).ReturnsForAnyArgs(failed.ToList());
        theSource.CountStaleOutgoingAsync(default, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(0);

        var step = new RecoverStaleOutgoingStep(theRuntime.Storage, theSource, NullLogger.Instance);
        var result = await executeAsync(step, 2);

        result.OutgoingFailed.ShouldBe(2);
        await theSource.Received(1).RestartStaleOutgoingAsync(
            Arg.Is<IReadOnlyList<Envelope>>(x => x.Count == 2 && failed.All(x.Contains)), Arg.Any<CancellationToken>());

        // Not stale any more, so they are not excluded from the count of what is left: a store that subtracts
        // the excluded ids (RavenDb, Cosmos DB) would take them off twice
        await theSource.Received(1).CountStaleOutgoingAsync(Arg.Any<DateTimeOffset?>(),
            Arg.Is<Guid[]>(x => x.Length == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task rows_whose_clock_could_not_be_restarted_are_still_excluded_from_the_count()
    {
        theRuntime.Options.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
        var failed = new[] { unsendable(), unsendable() };
        theSource.LoadStaleOutgoingAsync(default, default!, default, TestContext.Current.CancellationToken).ReturnsForAnyArgs(failed.ToList());
        theSource.RestartStaleOutgoingAsync(default!, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("database is down")));

        var step = new RecoverStaleOutgoingStep(theRuntime.Storage, theSource, NullLogger.Instance);
        var result = await executeAsync(step, 2);

        result.OutgoingFailed.ShouldBe(2);
        await theSource.Received(1).CountStaleOutgoingAsync(Arg.Any<DateTimeOffset?>(),
            Arg.Is<Guid[]>(x => x.Length == 2), Arg.Any<CancellationToken>());
    }
}
