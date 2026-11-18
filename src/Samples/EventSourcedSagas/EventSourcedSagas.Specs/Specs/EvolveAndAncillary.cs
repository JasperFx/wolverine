using Bobcat;
using EventSourcedSagas.Fulfillment;
using EventSourcedSagas.GiftCards;
using EventSourcedSagas.Returns;
using Wolverine;

namespace EventSourcedSagas.Specs;

// GH-5415: a saga written with Evolve(), and a saga kept in an ancillary store.

// The completion and compensation specifications here need JasperFx/jasperfx#1241 (an inherited Apply runs beside a
// type's own Evolve), which is not released yet. Until it is, the base type's Apply methods never run for an Evolve
// saga, so they stay Acceptance specs
[BobcatFeature("An event-sourced saga written with Evolve()")]
[Collection(nameof(FulfillmentCollection))]
public class an_event_sourced_saga_written_with_evolve(InlineFulfillmentServices services) : EventSourcedSagaSpec(services)
{
    private Guid ReturnRequestId { get; } = Guid.NewGuid();

    [BobcatSpec(typeof(RequestReturn))]
    public async Task requesting_a_return_starts_its_stream()
    {
        await WhenReceived(new RequestReturn(ReturnRequestId, OrderFulfillmentId, 100m));

        await ThenStreamIsStartedWithEvents<ReturnRequest>(new ReturnRequested(OrderFulfillmentId, 100m));
        await ThenSaga<ReturnRequest>(ReturnRequestId, Specify<ReturnRequest>()
            .With(x => x.Amount, 100m)
            .With(x => x.ItemsReceived, false));
    }

    [BobcatSpec(typeof(ReceiveReturnedItems))]
    public async Task receiving_the_items_continues_the_return()
    {
        await GivenEvents<ReturnRequest>(ReturnRequestId, new ReturnRequested(OrderFulfillmentId, 100m));

        await WhenReceived(new ReceiveReturnedItems(ReturnRequestId));

        ThenEvents(new ReturnedItemsReceived());
        await ThenSaga<ReturnRequest>(ReturnRequestId, Specify<ReturnRequest>().With(x => x.ItemsReceived, true));
    }

    [BobcatSpec(typeof(RefundReturn), Lifecycle = Lifecycle.Acceptance)]
    public async Task refunding_the_return_completes_it()
    {
        await GivenEvents<ReturnRequest>(ReturnRequestId, new ReturnRequested(OrderFulfillmentId, 100m), new ReturnedItemsReceived());

        await WhenReceived(new RefundReturn(ReturnRequestId));

        ThenEvents(new ReturnRefunded(100m), new SagaCompleted());
        await ThenTheSagaIsCompleted<ReturnRequest>(ReturnRequestId);
    }

    [BobcatSpec(typeof(RefundReturn), Lifecycle = Lifecycle.Acceptance)]
    public async Task a_refund_the_ledger_cannot_record_is_compensated()
    {
        await GivenEvents<ReturnRequest>(ReturnRequestId, new ReturnRequested(OrderFulfillmentId, 100m), new ReturnedItemsReceived());
        LedgerIsUnavailable();

        await WhenReceived(new RefundReturn(ReturnRequestId));

        await ThenDeadLettered<RefundReturn>(by: FulfillmentServices.ServiceName, tagged: "compensated");
        await ThenTheSagaStatusIs<ReturnRequest>(ReturnRequestId, SagaStatus.Compensated);
        await ThenTheSagaCompensated<ReturnRequest, RefundReturn, LedgerUnavailableException>(ReturnRequestId);
        await ThenSaga<ReturnRequest>(ReturnRequestId, Specify<ReturnRequest>()
            .With(x => x.AbandonedBecause, "The payment ledger was unavailable"));
    }
}

[BobcatFeature("An event-sourced saga kept in an ancillary store")]
[Collection(nameof(FulfillmentCollection))]
public class an_event_sourced_saga_in_an_ancillary_store(InlineFulfillmentServices services) : EventSourcedSagaSpec(services)
{
    private Guid GiftCardId { get; } = Guid.NewGuid();

    [BobcatSpec(typeof(IssueGiftCard))]
    public async Task issuing_a_gift_card_starts_its_stream_in_the_gift_card_store()
    {
        await WhenReceived(new IssueGiftCard(GiftCardId, 50m));

        await In<IGiftCardStore>().ThenStreamIsStartedWithEvents<GiftCard>(new GiftCardIssued(50m));
        await In<IGiftCardStore>().ThenTheSagaSnapshot<GiftCard>(GiftCardId, Specify<GiftCard>().With(x => x.Balance, 50m));
    }

    [BobcatSpec(typeof(RedeemGiftCard))]
    public async Task redeeming_the_whole_balance_completes_the_gift_card()
    {
        await In<IGiftCardStore>().GivenEvents<GiftCard>(GiftCardId, new GiftCardIssued(50m));

        await WhenReceived(new RedeemGiftCard(GiftCardId, 50m));

        ThenEvents(new GiftCardRedeemed(50m), new SagaCompleted());
        await In<IGiftCardStore>().ThenTheSagaIsCompleted<GiftCard>(GiftCardId);
    }
}

[BobcatFeature("An event-sourced saga kept in an ancillary Polecat store")]
[Collection(nameof(PolecatFulfillmentCollection))]
public class an_event_sourced_saga_in_an_ancillary_polecat_store(PolecatFulfillmentServices services) : EventSourcedSagaSpec(services)
{
    private Guid GiftCardId { get; } = Guid.NewGuid();

    [BobcatSpec(typeof(IssuePolecatGiftCard))]
    public async Task issuing_a_gift_card_starts_its_stream_in_the_gift_card_store()
    {
        await WhenReceived(new IssuePolecatGiftCard(GiftCardId, 50m));

        await In<IPolecatGiftCardStore>().ThenStreamIsStartedWithEvents<PolecatGiftCard>(new GiftCardIssued(50m));
        await In<IPolecatGiftCardStore>().ThenTheSagaSnapshot<PolecatGiftCard>(GiftCardId, Specify<PolecatGiftCard>().With(x => x.Balance, 50m));
    }

    [BobcatSpec(typeof(RedeemPolecatGiftCard))]
    public async Task redeeming_the_whole_balance_completes_the_gift_card()
    {
        await In<IPolecatGiftCardStore>().GivenEvents<PolecatGiftCard>(GiftCardId, new GiftCardIssued(50m));

        await WhenReceived(new RedeemPolecatGiftCard(GiftCardId, 50m));

        ThenEvents(new GiftCardRedeemed(50m), new SagaCompleted());
        await In<IPolecatGiftCardStore>().ThenTheSagaIsCompleted<PolecatGiftCard>(GiftCardId);
    }
}
