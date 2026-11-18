# Event-Sourced Sagas <Badge type="tip" text="7.0" />

An event-sourced saga is a [saga](/guide/durability/sagas) whose state is its own event stream in
[Marten](/guide/durability/marten/), [Polecat](/guide/durability/polecat/) or [Fisher](/guide/durability/fisher/),
instead of a stored document. You still write one class per process, with the same `Start`, `Handle` and `NotFound`
methods as any other saga. The difference is what those methods return: events, which Wolverine appends to the saga's
stream, and messages, which go out through the outbox. The saga's state is rebuilt from its events with the store's own
aggregation, so you get the whole history of every process for free, along with time travel to any step.

This replaces the hand-rolled recipe in [Process Manager via Handlers](/guide/durability/marten/process-manager-via-handlers),
and removes the friction that page lists.

## Your first event-sourced saga

Derive from `EventSourcedSaga` (which derives from [`CompensatingSaga`](/guide/durability/sagas#compensating-failed-steps)),
and rebuild the saga's state with `Apply` methods, just like any self-aggregating snapshot:

<!-- snippet: sample_event_sourced_saga -->
<a id='snippet-sample_event_sourced_saga'></a>
```cs
public class OrderFulfillment : EventSourcedSaga
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public decimal TotalAmount { get; set; }

    public bool IsPaid { get; set; }
    public bool AreItemsReserved { get; set; }
    public bool IsShipped { get; set; }
    public bool IsRefunded { get; set; }
    public string? CancellationReason { get; set; }

    // ---- starting -------------------------------------------------------------------------------------------------

    public static (OrderFulfillmentStarted, PaymentTimeout) Start(StartOrderFulfillment command)
        => (new OrderFulfillmentStarted(command.OrderFulfillmentId, command.CustomerId, command.TotalAmount),
            new PaymentTimeout(command.OrderFulfillmentId));

    // Starts the fulfillment if it has not started, and is handled like any other message if it has
    public IEnumerable<object> StartOrHandle(OrderPlaced placed)
    {
        if (Id != Guid.Empty) yield break;

        yield return new OrderFulfillmentStarted(placed.OrderFulfillmentId, placed.CustomerId, placed.TotalAmount);
    }

    // ---- continuing -----------------------------------------------------------------------------------------------

    // The step's own guard against a duplicate delivery stays: it is reliable because the snapshot is written with the events
    public IEnumerable<object> Handle(PaymentReceived payment, PaymentLedger ledger)
    {
        if (IsPaid) return [];
        if (payment.Amount != TotalAmount) throw new PaymentAmountMismatchException(TotalAmount, payment.Amount);

        ledger.Record(Id, payment.Amount);
        return completingAfter(new PaymentConfirmed(payment.Amount), AreItemsReserved && IsShipped);
    }

    public IEnumerable<object> Handle(WarehouseReservedItems reserved)
        => AreItemsReserved ? [] : completingAfter(new ItemsReserved(reserved.ReservationId), IsPaid && IsShipped);

    public IEnumerable<object> Handle(CarrierConfirmedShipment shipment)
        => IsShipped ? [] : completingAfter(new ShipmentConfirmed(shipment.TrackingNumber), IsPaid && AreItemsReserved);

    // MarkCompleted() completes the saga too: Wolverine appends SagaCompleted after the business event
    public OrderFulfillmentCancelled Handle(CancelOrderFulfillment command)
    {
        MarkCompleted();
        return new OrderFulfillmentCancelled(command.Reason);
    }

    public IEnumerable<object> Handle(PaymentTimeout timeout)
        => IsPaid ? [] : [new OrderFulfillmentCancelled("Payment timed out"), new SagaCompleted()];

    // The last step completes the fulfillment: its own event, then Wolverine's SagaCompleted
    private static IEnumerable<object> completingAfter(object step, bool everyOtherStepIsDone)
        => everyOtherStepIsDone ? [step, new SagaCompleted()] : [step];

    // ---- after completion -----------------------------------------------------------------------------------------

    // A shipment confirmed for a fulfillment that has already completed or been cancelled
    public static ShipmentForClosedOrder NotFound(CarrierConfirmedShipment shipment)
        => new(shipment.OrderFulfillmentId, shipment.TrackingNumber);

    // ---- compensation ---------------------------------------------------------------------------------------------

    // The ledger stayed unavailable through the retries
    public (RefundIssued, OutgoingMessages) Compensate(PaymentReceived failed, LedgerUnavailableException ex)
        => refund(failed.Amount);

    // A wrong amount, compensated at once. Refunding twice is refused, which fails the compensation itself
    public (RefundIssued, OutgoingMessages) Compensate(PaymentReceived failed, PaymentAmountMismatchException ex)
        => IsRefunded ? throw new RefundAlreadyIssuedException(Id) : refund(failed.Amount);

    private (RefundIssued, OutgoingMessages) refund(decimal amount)
        => (new RefundIssued(amount), [new RefundPayment(Id, amount)]);

    // ---- the state, rebuilt from the stream -----------------------------------------------------------------------

    public void Apply(OrderFulfillmentStarted e)
    {
        Id = e.OrderFulfillmentId;
        CustomerId = e.CustomerId;
        TotalAmount = e.TotalAmount;
    }

    public void Apply(PaymentConfirmed e) => IsPaid = true;

    public void Apply(ItemsReserved e) => AreItemsReserved = true;

    public void Apply(ShipmentConfirmed e) => IsShipped = true;

    public void Apply(RefundIssued e) => IsRefunded = true;

    public void Apply(OrderFulfillmentCancelled e) => CancellationReason = e.Reason;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EventSourcedSagas/EventSourcedSagas.Specs/Fulfillment/OrderFulfillment.cs#L15-L112' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_event_sourced_saga' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

That's all the configuration there is. Integrate the event store with Wolverine as usual (`IntegrateWithWolverine()`),
and Wolverine:

* registers `OrderFulfillment` as an **Inline** snapshot of its own stream, written in the same transaction as its events,
  so loading the saga for each message is one cheap read
* loads the saga's stream for writing with the store's `FetchForWriting` before each message, with optimistic concurrency
* appends whatever events the method returned, and sends whatever messages it returned, in one transaction

## Starting an event-sourced saga

`Start` returns events and messages, **never the saga itself**. The starting state comes only from applying those
events, so it can never disagree with the stream. The stream's id is the saga id, found on the starting message the
same way as for any saga: an `{SagaName}Id` or `Id` member, `[SagaIdentity]`, or the saga-id header.

* `StartOrHandle` (or an instance `Start`) gets a new, empty saga when the stream does not exist yet, and the existing
  saga when it does.
* A `Start` for a stream that already exists is refused with a `SagaConcurrencyException`, as a duplicate start of a
  document saga is. That includes losing a race to another message that started the same stream between the load and
  the commit: whatever each store throws, your error policies only ever see `SagaConcurrencyException`.

## What the methods return

The rules are the [aggregate handler workflow's](/guide/durability/marten/event-sourcing):

| Returned | Becomes |
|---|---|
| A single object, or a tuple of them | an event on the saga's stream |
| `IEnumerable<object>`, `object[]`, `Events`, `EventsToAppend`, `IAsyncEnumerable<object>` | events on the saga's stream |
| `OutgoingMessages`, a `TimeoutMessage`, anything else that is `ISendMyself` | messages, through the outbox |
| What a `NotFound` method returns | messages: there is no stream to append to |

So a `PaymentTimeout : TimeoutMessage` returned from `Start` is scheduled, while `OrderFulfillmentStarted` is appended.

`dotnet run -- saga-flow` draws the saga with the events each method appends to its stream, next to the messages it
sends; see [Drawing a Saga's Flow](/guide/durability/sagas#drawing-a-sagas-flow). An event the signature names, such as
a single returned event or a tuple member, is drawn. Events in an `IEnumerable<object>` are only known at run time, so
return them as a tuple where you want them in the picture.

## Completing an event-sourced saga

A saga is completed once Wolverine's own `SagaCompleted` event is in its stream. There are two ways to get it there,
and they end the same way:

* return `new SagaCompleted()` among a handler's events, as `completingAfter()` does above
* call `MarkCompleted()` in a handler (or a `Compensate` method), and Wolverine appends `SagaCompleted` after it runs:

<!-- snippet: sample_event_sourced_saga_completed_by_MarkCompleted -->
<a id='snippet-sample_event_sourced_saga_completed_by_MarkCompleted'></a>
```cs
[ArchiveWhenCompleted]
public class SupportTicket : EventSourcedSaga
{
    public Guid Id { get; set; }
    public string Subject { get; set; } = "";

    public static TicketOpened Start(OpenTicket open) => new(open.Subject);

    public void Handle(CloseTicket close) => MarkCompleted();

    public void Apply(TicketOpened e) => Subject = e.Subject;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EventSourcedSagas/EventSourcedSagas.Specs/Support/SupportTicket.cs#L15-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_event_sourced_saga_completed_by_MarkCompleted' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `EventSourcedSaga` base type applies `SagaCompleted` itself and sets `CompletedAt` to the event's timestamp, so
your business events stay business events. Nothing is deleted when a saga completes: the stream and the snapshot both
stay. Wolverine checks for completion before any `Handle` method runs, so no handler needs a "have I finished already?"
guard. A message for a completed saga goes to the saga's `NotFound` method for that message type if it has one, a
`TimeoutMessage` is quietly dropped, and anything else is treated as a message for a missing saga.

A stream archived with JasperFx.Events' `Archived` event counts as completed too, so a saga stream someone archives by
hand is done.

## Writing the saga with `Evolve()`

Instead of `Apply` methods, a saga can declare one `Evolve(IEvent e)` (or `Evolve(object e)`) method, as any
self-aggregating type can:

<!-- snippet: sample_event_sourced_saga_with_evolve -->
<a id='snippet-sample_event_sourced_saga_with_evolve'></a>
```cs
public class ReturnRequest : EventSourcedSaga
{
    public Guid Id { get; set; }
    public Guid OrderFulfillmentId { get; set; }
    public decimal Amount { get; set; }
    public bool ItemsReceived { get; set; }
    public bool IsRefunded { get; set; }
    public string? AbandonedBecause { get; set; }

    public static ReturnRequested Start(RequestReturn request) => new(request.OrderFulfillmentId, request.Amount);

    public ReturnedItemsReceived Handle(ReceiveReturnedItems received) => new();

    // Refunding the return ends it
    public IEnumerable<object> Handle(RefundReturn refund, PaymentLedger ledger)
    {
        ledger.Record(Id, Amount);
        return [new ReturnRefunded(Amount), new SagaCompleted()];
    }

    public ReturnRefundAbandoned Compensate(RefundReturn failed, LedgerUnavailableException ex)
        => new("The payment ledger was unavailable");

    public void Evolve(IEvent e)
    {
        switch (e.Data)
        {
            case ReturnRequested requested:
                Id = e.StreamId;
                OrderFulfillmentId = requested.OrderFulfillmentId;
                Amount = requested.Amount;
                break;
            case ReturnedItemsReceived _:
                ItemsReceived = true;
                break;
            case ReturnRefunded _:
                IsRefunded = true;
                break;
            case ReturnRefundAbandoned abandoned:
                AbandonedBecause = abandoned.Reason;
                break;
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EventSourcedSagas/EventSourcedSagas.Specs/Returns/ReturnRequest.cs#L25-L70' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_event_sourced_saga_with_evolve' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine's own events (`SagaCompleted` and the compensation events below) still reach the base type's `Apply` methods:
an inherited `Apply` runs first, then your `Evolve`. You never forward them by hand.

::: warning
That needs the JasperFx.Events source generator 3.8.0 or later
([jasperfx#1241](https://github.com/JasperFx/jasperfx/issues/1241)). A store package may bundle an older copy of the
generator (Marten 10.0.0-alpha.3 does), in which case an `Evolve()` saga never sees Wolverine's own events: it never
reads as completed, and its compensation status stays `Active`. Reference the generator package directly, and its copy
wins over the bundled one:

```xml
<PackageReference Include="JasperFx.Events.SourceGenerator" Version="3.8.0"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" PrivateAssets="all" />
```
:::

Use type patterns with a variable or a discard in `Evolve` (`case ReturnedItemsReceived _:`): the JasperFx.Events
source generator reads the event types an `Evolve` handles from its `case` labels, and does not recognise a bare
`case ReturnedItemsReceived:`.

## Snapshots

Wolverine registers each event-sourced saga as an Inline snapshot in the store the saga type belongs to. If you
register the saga yourself, your registration wins, with one exception:

* **Inline** (Wolverine's default): the saga explorer in CritterWatch can open, list and search the sagas.
* **Live** (`LiveStreamAggregation<T>()`) works too: the saga is aggregated from its stream for every message, and the
  saga explorer can open one at a time but cannot search them.
* **Async** is refused at startup. A saga that read a snapshot behind its own stream would see stale state and get the
  completion check wrong.

Register the saga before `IntegrateWithWolverine()`, or inside `AddMarten()` / `AddPolecat()` / `AddFisher()`, so
Wolverine sees your registration.

### Identity

An event-sourced saga is keyed like a stream: by a `Guid` or a `string`, or a strong-typed id wrapping one. Wolverine
refuses at startup an `int` or `long` id, and an id that does not match the store's stream identity (a `string` id on a
store keyed `AsGuid`, or the reverse).

### Ancillary stores

A saga type picks its store as an aggregate handler does. Mark it with `[MartenStore(typeof(IOtherStore))]`,
`[PolecatStore(...)]` or `[Storage(...)]`, and Wolverine registers its snapshot in that store and commits its events
there.

## Compensation

[`Compensate` methods](/guide/durability/sagas#compensating-failed-steps) work on an event-sourced saga as on any
`CompensatingSaga`, except that they return events and messages, like `Handle`. Wolverine records the compensation as
events of its own on the saga's stream:

| Event | Alias in the store | Means |
|---|---|---|
| `SagaCompensating(Step, ExceptionType)` | `wolverine_saga_compensating` | a compensation of the failed `Step` began |
| `SagaCompensated(Step, ExceptionType)` | `wolverine_saga_compensated` | it succeeded |
| `SagaCompensationFailed(Step, ExceptionType, Reason)` | `wolverine_saga_compensation_failed` | it failed after its retries |
| `SagaCompleted` | `wolverine_saga_completed` | the saga completed |

`SagaCompensating`, the events `Compensate` returned and `SagaCompensated` are appended in one transaction. The base
type rebuilds `Status` and `CompensationLog` from them, so time travel shows the status at every step. A concurrency
conflict saving the compensation (another message appended to the stream meanwhile) is retried under the compensation's
error policies, and if it never succeeds the saga is marked `CompensationFailed` in a transaction of its own.

## Keeping and archiving completed sagas

With [retention](/guide/durability/sagas#keeping-completed-sagas) on (`opts.Policies.RetainCompletedSagas(...)` or
`[RetainWhenCompleted]`), the same sweep agent that deletes retained document sagas **archives** a completed
event-sourced saga's stream once its retention period has passed, by appending JasperFx.Events' `Archived` event
(reason `saga completed`), and removes its snapshot. Events are never deleted. With no retention policy, completed
streams simply stay, which is the event store's normal default.

To archive a saga's stream as soon as it completes, mark the saga type `[ArchiveWhenCompleted]`: Wolverine appends
`Archived` together with `SagaCompleted`.

::: warning
Only use `[ArchiveWhenCompleted]` for sagas nothing reads asynchronously. The async daemon skips archived events unless
a projection or subscription sets `IncludeArchivedEvents`, so an async projection, a subscription or event forwarding
that hadn't caught up when a short saga completed would never see its events.
:::

## Publishing a saga's events

The events a saga appends go into its own stream. Putting them on the bus works as for any aggregate: opt in per event
type with [event forwarding or subscriptions](/guide/durability/marten/event-forwarding). Messages a saga returns go
through the outbox as for any saga.

## History and time travel

Event-sourced saga storage implements `ISagaHistoryDiagnostics`, reached as
`runtime.SagaStorage as ISagaHistoryDiagnostics`, and reports `SagaDescriptor.SupportsHistory`:

* `ReadSagaHistoryAsync(sagaTypeName, id, new SagaHistoryPageRequest(page, pageSize))` returns the stream's events in
  order, one page at a time: version, timestamp, the store's event type name, the .NET type and the event body as JSON.
* `ReadSagaAtAsync(sagaTypeName, id, version)` and `ReadSagaAtAsync(sagaTypeName, id, timestamp)` return the saga as it
  was at that point, using the store's own time travel.

CritterWatch uses these for the saga timeline and the "state at step N" view. A saga kept as a document can record a
message history that is read the same way, without the time travel; see
[recording a saga's message history](/guide/durability/sagas#recording-a-saga-s-message-history).

## Testing

Everything above is specified with [Bobcat](/guide/testing-with-bobcat) in
`src/Samples/EventSourcedSagas`, on Marten, Polecat and Fisher. Arrange an event-sourced saga with
`GivenEvents<TSaga>(id, ...)` rather than `GivenSaga<TSaga>()`, and fire its timeouts with
`WhenScheduledMessagesAreExecuted()` (`ITrackedSession.PlayScheduledMessagesAsync()` outside Bobcat). Bobcat steps made
for event-sourced sagas include `ThenTheSagaIsCompleted`, `ThenTheSagaSnapshot`, `ThenTheSagaIsKeptAs`,
`WhenCompletedSagasAreSwept`, `ThenTheSagaStreamIsArchived`, `WhenTheSagaHistoryIsRead`, `WhenTheSagaIsReadAtVersion`,
`WhileActingOn<T>` (another message appends to the same stream while the act runs) and
`WhileCompensatingAnotherMessageAppends<T>`.

## Limitations in 7.0

* `IdNotProvided` methods are not supported on an event-sourced saga yet.
* No Dynamic Consistency Boundary (`[BoundaryModel]`) in an event-sourced saga's handlers: a saga's consistency
  boundary is its own stream. Send a command to a `[BoundaryModel]` handler instead.
* F# code generation does not support event-sourced sagas yet.
