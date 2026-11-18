using Bobcat;
using SagaWorkflows.MoneyTransfer;
using SagaWorkflows.MoneyTransfer.Fraud;
using Wolverine;

namespace SagaWorkflows.Specs;

// GH-5672: a Compensate method whose failed message is an interface or a base type covers every concrete message the
// saga sends that is assignable to it, not only the saga's own handlers. Fault<T> is not covariant, so Wolverine files
// the method under the fault of each concrete type it knows: the messages the application handles and returns, and those
// routed with PublishMessage<T>(). [Compensates] names the ones it cannot know, such as a message sent through
// IMessageBus and routed by namespace. Selection is unchanged: an exact type beats an interface.

[BobcatFeature("One Compensate method answers the failure of any step of a transfer")]
[Collection(nameof(DocumentSagaCollection))]
public class compensating_a_family_of_steps(DocumentSagaServices services) : SagaWorkflowSpec(services)
{
    private readonly Guid _transferId = Guid.NewGuid();

    [BobcatSpec(typeof(TransferMoney))]
    public async Task a_withdrawal_refused_for_insufficient_funds_fails_the_transfer_with_nothing_to_undo()
    {
        AccountHolds("alice", 50m);

        await WhenReceived(new TransferMoney(_transferId, "alice", "bob", 100m));

        ThenDeadLettered<Withdraw>(by: WorkflowServices.ProvidersName);
        await ThenTheSagaCompensated<Transfer, Withdraw, InsufficientFundsException>(_transferId);
        ThenMessageSent(new TransferFailed(_transferId, "Account alice has insufficient funds"));
        ThenNoMessageSent<RefundWithdrawal>();
        await ThenTheSagaStatusIs<Transfer>(_transferId, SagaStatus.Compensated);
    }

    [BobcatSpec(typeof(TransferMoney))]
    public async Task a_transfer_the_ledger_cannot_record_is_reversed_newest_first()
    {
        AccountHolds("alice", 500m);
        TheLedgerRefuses(_transferId);

        await WhenReceived(new TransferMoney(_transferId, "alice", "bob", 100m));

        ThenAttempted<RecordTransfer>(3, by: WorkflowServices.ProvidersName);
        await ThenTheSagaCompensated<Transfer, RecordTransfer, LedgerUnavailableException>(_transferId);
        ThenMessageSent(new ReverseDeposit(_transferId, "bob", 100m));
        ThenMessageSent(new RefundWithdrawal(_transferId, "alice", 100m));
        ThenMessageSent(new TransferFailed(_transferId, "The ledger is unavailable"));
        ThenAccountHolds("alice", 500m);
        ThenAccountHolds("bob", 0m);
    }

    // Compensate(Deposit, ExceptionInfo) is more specific than Compensate(ITransferStep, ExceptionInfo)
    [BobcatSpec(typeof(TransferMoney))]
    public async Task a_refused_deposit_is_compensated_by_the_method_for_its_exact_type()
    {
        AccountHolds("alice", 500m);
        AccountIsClosed("bob");

        await WhenReceived(new TransferMoney(_transferId, "alice", "bob", 100m));

        await ThenTheSagaCompensated<Transfer, Deposit, AccountClosedException>(_transferId);
        ThenMessageSent(new RefundWithdrawal(_transferId, "alice", 100m));
        ThenMessageSent(new TransferFailed(_transferId, "The deposit to bob was refused: Account bob is closed"));
        ThenAccountHolds("alice", 500m);
    }

    // ScreenTransfer is sent through IMessageBus and routed by a namespace rule, so only [Compensates] covers it
    [BobcatSpec(typeof(TransferMoney))]
    public async Task a_step_sent_through_the_message_bus_is_compensated_through_Compensates()
    {
        AccountHolds("alice", 50_000m);
        FraudSuspected("alice");

        await WhenReceived(new TransferMoney(_transferId, "alice", "bob", 20_000m));

        ThenDeadLettered<ScreenTransfer>(by: WorkflowServices.ProvidersName);
        await ThenTheSagaCompensated<Transfer, ScreenTransfer, SuspectedFraudException>(_transferId);
        ThenMessageSent(new RefundWithdrawal(_transferId, "alice", 20_000m));
        ThenNoMessageSent<Deposit>();
        ThenAccountHolds("alice", 50_000m);
    }

    [BobcatSpec(typeof(TransferMoney))]
    public async Task a_transfer_that_succeeds_compensates_nothing()
    {
        AccountHolds("alice", 500m);

        await WhenReceived(new TransferMoney(_transferId, "alice", "bob", 100m));

        ThenMessageSent(new TransferCompleted(_transferId));
        ThenNoMessageSent<TransferFailed>();
        ThenAccountHolds("bob", 100m);
        await ThenNoSaga<Transfer>(_transferId);
    }

    // What describe and CritterWatch show: the concrete types each Compensate covers. Deposit is covered by both
    // methods; the exact one wins when a deposit fails
    [BobcatSpec]
    public async Task the_service_describes_which_steps_each_Compensate_method_covers()
    {
        await ThenTheCompensateMethodCovers<Transfer>("Compensate(ITransferStep, ExceptionInfo)",
            typeof(Deposit), typeof(RecordTransfer), typeof(ScreenTransfer), typeof(Withdraw));
        await ThenTheCompensateMethodCovers<Transfer>("Compensate(Deposit, ExceptionInfo)", typeof(Deposit));
    }
}

[BobcatFeature("One Compensate method answers the failure of any step of an event-sourced transfer")]
[Collection(nameof(EventSourcedSagaCollection))]
public class compensating_a_family_of_steps_on_an_event_sourced_saga(EventSourcedSagaServices services)
    : SagaWorkflowSpec(services)
{
    private readonly Guid _transferId = Guid.NewGuid();

    // The event-sourced transfer sends its steps in OutgoingMessages; the PublishMessage<T>() rules make them known
    [BobcatSpec(typeof(TransferMoney))]
    public async Task a_transfer_the_ledger_cannot_record_is_reversed_newest_first()
    {
        AccountHolds("alice", 500m);
        TheLedgerRefuses(_transferId);

        await WhenReceived(new TransferMoney(_transferId, "alice", "bob", 100m));

        await ThenTheSagaCompensated<EventSourcedTransfer, RecordTransfer, LedgerUnavailableException>(_transferId);
        ThenMessageSent(new ReverseDeposit(_transferId, "bob", 100m));
        ThenMessageSent(new RefundWithdrawal(_transferId, "alice", 100m));
        await ThenSaga<EventSourcedTransfer>(_transferId, Specify<EventSourcedTransfer>()
            .With(x => x.UndoneBecause, "The ledger is unavailable"));
        ThenAccountHolds("alice", 500m);
    }

    [BobcatSpec(typeof(TransferMoney))]
    public async Task a_refused_deposit_is_compensated_by_the_method_for_its_exact_type()
    {
        AccountHolds("alice", 500m);
        AccountIsClosed("bob");

        await WhenReceived(new TransferMoney(_transferId, "alice", "bob", 100m));

        await ThenTheSagaCompensated<EventSourcedTransfer, Deposit, AccountClosedException>(_transferId);
        ThenMessageSent(new TransferFailed(_transferId, "The deposit to bob was refused: Account bob is closed"));
        ThenAccountHolds("alice", 500m);
    }

    [BobcatSpec(typeof(TransferMoney))]
    public async Task a_step_sent_through_the_message_bus_is_compensated_through_Compensates()
    {
        AccountHolds("alice", 50_000m);
        FraudSuspected("alice");

        await WhenReceived(new TransferMoney(_transferId, "alice", "bob", 20_000m));

        await ThenTheSagaCompensated<EventSourcedTransfer, ScreenTransfer, SuspectedFraudException>(_transferId);
        ThenMessageSent(new RefundWithdrawal(_transferId, "alice", 20_000m));
        ThenAccountHolds("alice", 50_000m);
    }

    [BobcatSpec]
    public async Task the_service_describes_which_steps_each_Compensate_method_covers()
    {
        await ThenTheCompensateMethodCovers<EventSourcedTransfer>("Compensate(ITransferStep, ExceptionInfo)",
            typeof(Deposit), typeof(RecordTransfer), typeof(ScreenTransfer), typeof(Withdraw));
    }
}
