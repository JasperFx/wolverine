using Bobcat;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using SagaWorkflows.Providers;
using Wolverine;
using Wolverine.Bobcat;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;

namespace SagaWorkflows.Specs;

/// <summary>
/// The base of every specification here: acts in the workflows service, also tracks the providers service, and treats
/// a dead letter as an outcome to judge rather than a failed act.
/// </summary>
public abstract class SagaWorkflowSpec : WolverineSpec, IAsyncLifetime
{
    private readonly WorkflowServices _services;

    protected SagaWorkflowSpec(WorkflowServices services) : base(services.Workflows)
    {
        _services = services;

        AlsoTrack(services.Providers);
        DeadLettersFailTheAct = false;

        // Retries in the providers service, hops over TCP and back, a saga load and a compensation in workflows
        Timeout = TimeSpan.FromSeconds(30);
    }

    public async ValueTask InitializeAsync() => await _services.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Bank Bank => _services.Providers.Services.GetRequiredService<Bank>();

    // ---- the bank's state ---------------------------------------------------------------------------------------------

    [Given("account {account} holds {balance}")]
    public void AccountHolds(string account, decimal balance) => Bank.Open(account, balance);

    [Given("account {account} is closed")]
    public void AccountIsClosed(string account) => Bank.ClosedAccounts.Add(account);

    [Given("the ledger refuses to record transfer {transferId}")]
    public void TheLedgerRefuses(Guid transferId) => Bank.LedgerRefuses.Add(transferId);

    private TravelProviders Travel => _services.Providers.Services.GetRequiredService<TravelProviders>();

    [Given("the {booking} provider refuses {customer}")]
    public void ProviderRefuses(Booking booking, string customer) => Travel.Refusals.Add((booking, customer));

    [Given("the hotel has no rooms for {customer}")]
    public void HotelIsFull(string customer) => Travel.FullHotels.Add(customer);

    [Given("the {booking} provider refuses to cancel for {customer}")]
    public void ProviderRefusesToCancel(Booking booking, string customer)
        => Travel.CancellationRefusals.Add((booking, customer));

    [Then("{customer} holds no {booking} booking")]
    public void ThenNoBooking(string customer, Booking booking)
        => SpecAssert.Check(booking.ToString(), Travel.HasReservation(booking, customer) ? "booked" : "not booked", "not booked");

    [Then("{customer} still holds a {booking} booking")]
    public void ThenStillBooked(string customer, Booking booking)
        => SpecAssert.Check(booking.ToString(), Travel.HasReservation(booking, customer) ? "booked" : "not booked", "booked");

    [Given("large transfers from {account} look fraudulent")]
    public void FraudSuspected(string account) => Bank.FraudSuspects.Add(account);

    [Then("account {account} holds {balance}")]
    public void ThenAccountHolds(string account, decimal balance)
        => SpecAssert.Check("balance", Bank.BalanceOf(account), balance);

    // ---- the undo stack (GH-5673) -------------------------------------------------------------------------------------

    private async Task<TSaga?> loadSaga<TSaga>(Guid sagaId) where TSaga : CompensatingSaga
    {
        await using var session = _services.Workflows.Services.GetRequiredService<IDocumentStore>().QuerySession();
        return await session.LoadAsync<TSaga>(sagaId);
    }

    /// <summary>The steps whose undo messages are still on the saga's undo stack, oldest first</summary>
    [Then("the undo stack of saga {sagaId} holds {steps}")]
    public async Task ThenTheUndoStackHolds<TSaga>(Guid sagaId, params string[] steps) where TSaga : CompensatingSaga
    {
        var saga = await loadSaga<TSaga>(sagaId);
        SpecAssert.Check("undo stack", saga == null ? "(no saga)" : string.Join(", ", saga.UndoStack.Select(x => x.Tag)),
            string.Join(", ", steps));
    }

    /// <summary>The saga's last compensation sent the undo messages of these steps, in this order</summary>
    [Then("the last compensation of saga {sagaId} undid {steps}")]
    public async Task ThenTheLastCompensationUndid<TSaga>(Guid sagaId, params string[] steps) where TSaga : CompensatingSaga
    {
        var saga = await loadSaga<TSaga>(sagaId);
        var entry = saga?.CompensationLog.LastOrDefault();
        SpecAssert.Check("undone", entry == null ? "(no compensation)" : string.Join(", ", (entry.Undone ?? []).Select(x => x.Step)),
            string.Join(", ", steps));
    }

    /// <summary>
    /// The undo messages were handled in this order, and each one was sent only after the one before it was handled: a
    /// sequential unwind, not a batch
    /// </summary>
    [Then("{undoMessages} are handled one at a time, in that order")]
    public void ThenUndoneOneAtATime(params Type[] undoMessages)
    {
        var records = LastAct.Session?.AllRecordsInOrder() ?? [];
        int indexOf(Type type, MessageEventType kind)
            => Array.FindIndex(records, x => x.MessageEventType == kind && type.IsInstanceOfType(x.Message));

        var handled = undoMessages.Select(x => indexOf(x, MessageEventType.MessageSucceeded)).ToArray();
        SpecAssert.Fact(handled.All(x => x >= 0),
            $"Expected every undo message to be handled, but {string.Join(", ", undoMessages.Where((_, i) => handled[i] < 0).Select(x => x.Name))} never was");

        for (var i = 1; i < undoMessages.Length; i++)
        {
            var sent = indexOf(undoMessages[i], MessageEventType.Sent);
            SpecAssert.Fact(sent > handled[i - 1],
                $"Expected {undoMessages[i].Name} to be sent only after {undoMessages[i - 1].Name} was handled");
        }
    }

    // ---- the workflows service's schedule -----------------------------------------------------------------------------

    /// <summary>
    /// GH-5674. How many <paramref name="messageType" /> messages the saga <paramref name="sagaId" /> has waiting in the
    /// workflows service's durable schedule.
    /// </summary>
    [Then("{count} {messageType} of saga {sagaId} waiting in the schedule")]
    public async Task ThenWaitingInTheSchedule(Type messageType, object sagaId, int count)
    {
        var storage = _services.Workflows.Services.GetRequiredService<IWolverineRuntime>().Storage;
        var results = await storage.ScheduledMessages.QueryAsync(new ScheduledMessageQuery
        {
            SagaId = sagaId.ToString(),
            MessageType = messageType.ToMessageTypeName()
        }, CancellationToken.None);

        SpecAssert.Check("waiting", results.TotalCount, count);
    }

    /// <summary>The act sent exactly <paramref name="times" /> messages of <paramref name="messageType" />.</summary>
    [Then("exactly {times} {messageType} sent")]
    public void ThenSentExactly(Type messageType, int times)
        => SpecAssert.Check("sent", TheMessagesSent.Count(messageType.IsInstanceOfType), times);

    // ---- what the workflows service says about its sagas --------------------------------------------------------------

    /// <summary>
    /// GH-5672. The service's description (what <c>describe</c> and CritterWatch read) lists the <c>Compensate</c>
    /// method of <typeparamref name="TSaga" /> for <paramref name="failedMessageType" /> as compensating exactly
    /// <paramref name="messageTypes" />.
    /// </summary>
    [Then("the Compensate method for {failedMessageType} compensates {messageTypes}")]
    public async Task ThenTheCompensateMethodCovers<TSaga>(Type failedMessageType, params Type[] messageTypes)
        where TSaga : Saga
    {
        var capabilities = await ServiceCapabilities.ReadFrom(
            _services.Workflows.Services.GetRequiredService<IWolverineRuntime>(), null, CancellationToken.None);

        var saga = capabilities.Sagas.SingleOrDefault(x => x.StateType.FullName == typeof(TSaga).FullName);
        var compensation = saga?.Compensations.FirstOrDefault(x => x.FailedMessageType.FullName == failedMessageType.FullName);

        SpecAssert.Fact(compensation is not null,
            $"Expected {typeof(TSaga).Name} to describe a Compensate method for {failedMessageType.Name}, but it describes " +
            (saga is null ? "no such saga" : string.Join(", ", saga.Compensations.Select(x => x.FailedMessageType.Name))));

        var covered = compensation?.Covers.Select(x => x.Name).Order(StringComparer.Ordinal).ToArray() ?? [];
        SpecAssert.Check("compensates", string.Join(", ", covered),
            string.Join(", ", messageTypes.Select(x => x.NameInCode()).Order(StringComparer.Ordinal)));
    }
}
