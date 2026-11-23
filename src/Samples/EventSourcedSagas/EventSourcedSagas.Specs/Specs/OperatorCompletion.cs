using EventSourcedSagas.Fulfillment;
using EventSourcedSagas.Support;
using Wolverine;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;

namespace EventSourcedSagas.Specs;

// GH-5839: an operator tool (CritterWatch) completes an event-sourced saga from outside its handlers through
// IWolverineRuntime.SagaStorage. The saga gets the same SagaCompleted event MarkCompleted() appends, carrying the
// operator's reason, and the Archived event too for an [ArchiveWhenCompleted] saga such as SupportTicket. Nothing is
// deleted. Run on Marten (Inline and Live), Polecat and Fisher by the classes below.
public abstract class completing_an_event_sourced_saga_from_outside_its_handlers_5839(FulfillmentServices services)
    : IAsyncLifetime
{
    private const string Reason = "the customer withdrew the complaint by phone";

    private static readonly string SupportTicketName = typeof(SupportTicket).FullName!;

    private static readonly string OrderFulfillmentName = typeof(OrderFulfillment).FullName!;

    private Guid TicketId { get; } = Guid.NewGuid();

    public async ValueTask InitializeAsync() => await services.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private ISagaStoreDiagnostics sagas => services.Host.GetRuntime().SagaStorage;

    private Task<SagaTerminationResult> completeAsync(object identity)
        => sagas.CompleteSagaAsync(SupportTicketName, identity, Reason, TestContext.Current.CancellationToken);

    private Task openTicketAsync() => services.Host.InvokeMessageAndWaitAsync(new OpenTicket(TicketId, "Damaged parcel"));

    [Fact]
    public async Task completing_the_saga_appends_SagaCompleted_with_the_reason()
    {
        var fulfillmentId = Guid.NewGuid();
        await services.Host.InvokeMessageAndWaitAsync(new StartOrderFulfillment(fulfillmentId, Guid.NewGuid(), 100m));

        var result = await sagas.CompleteSagaAsync(OrderFulfillmentName, fulfillmentId, Reason,
            TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(SagaTerminationOutcome.CompletionAppended);
        result.Terminated.ShouldBeTrue();

        var state = await sagas.ReadSagaAsync(OrderFulfillmentName, fulfillmentId, TestContext.Current.CancellationToken);
        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeTrue();

        // Nothing is deleted: the stream keeps its events, and ends with the completion and the operator's reason
        var history = await ((ISagaHistoryDiagnostics)sagas).ReadSagaHistoryAsync(OrderFulfillmentName, fulfillmentId,
            new SagaHistoryPageRequest(), TestContext.Current.CancellationToken);

        history.Events.First().DotNetTypeName.ShouldBe(typeof(OrderFulfillmentStarted).FullName);
        var completed = history.Events.Last();
        completed.DotNetTypeName.ShouldBe(typeof(SagaCompleted).FullName);
        completed.Data.EnumerateObject()
            .Single(x => x.Name.Equals(nameof(SagaCompleted.Reason), StringComparison.OrdinalIgnoreCase))
            .Value.GetString().ShouldBe(Reason);
    }

    // SupportTicket is [ArchiveWhenCompleted], so its stream is archived along with the completion, as MarkCompleted()
    // would archive it
    [Fact]
    public async Task completing_an_ArchiveWhenCompleted_saga_archives_its_stream()
    {
        await openTicketAsync();

        var result = await completeAsync(TicketId);

        result.Outcome.ShouldBe(SagaTerminationOutcome.CompletionAppended);

        var state = await sagas.ReadSagaAsync(SupportTicketName, TicketId, TestContext.Current.CancellationToken);
        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeTrue();

        // An archived stream is out of the history read's sight
        var history = await ((ISagaHistoryDiagnostics)sagas).ReadSagaHistoryAsync(SupportTicketName, TicketId,
            new SagaHistoryPageRequest(), TestContext.Current.CancellationToken);
        history.Events.ShouldNotContain(x => x.DotNetTypeName == typeof(TicketOpened).FullName);
    }

    [Fact]
    public async Task the_identity_may_be_given_as_text()
    {
        await openTicketAsync();

        var result = await completeAsync(TicketId.ToString());

        result.Outcome.ShouldBe(SagaTerminationOutcome.CompletionAppended);
    }

    [Fact]
    public async Task completing_it_again_says_it_already_completed()
    {
        await openTicketAsync();
        await completeAsync(TicketId);

        var again = await completeAsync(TicketId);

        again.Outcome.ShouldBe(SagaTerminationOutcome.AlreadyCompleted);
        again.Terminated.ShouldBeFalse();
    }

    [Fact]
    public async Task a_saga_that_was_never_started_is_not_found()
    {
        var result = await completeAsync(Guid.NewGuid());

        result.Outcome.ShouldBe(SagaTerminationOutcome.NotFound);
    }

    [Fact]
    public void completion_is_offered()
    {
        sagas.SupportsSagaCompletion(SupportTicketName).ShouldBeTrue();
    }
}

[Collection(nameof(FulfillmentCollection))]
public class completing_an_event_sourced_saga_from_outside_its_handlers_on_marten_5839(InlineFulfillmentServices services)
    : completing_an_event_sourced_saga_from_outside_its_handlers_5839(services);

[Collection(nameof(LiveFulfillmentCollection))]
public class completing_a_live_event_sourced_saga_from_outside_its_handlers_on_marten_5839(LiveFulfillmentServices services)
    : completing_an_event_sourced_saga_from_outside_its_handlers_5839(services);

[Collection(nameof(PolecatFulfillmentCollection))]
public class completing_an_event_sourced_saga_from_outside_its_handlers_on_polecat_5839(PolecatFulfillmentServices services)
    : completing_an_event_sourced_saga_from_outside_its_handlers_5839(services);

[Collection(nameof(FisherFulfillmentCollection))]
public class completing_an_event_sourced_saga_from_outside_its_handlers_on_fisher_5839(FisherFulfillmentServices services)
    : completing_an_event_sourced_saga_from_outside_its_handlers_5839(services);
