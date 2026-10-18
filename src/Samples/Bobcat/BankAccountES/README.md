# Bank Account (Event Sourcing) -- Critter Stack Sample

## Domain Reference

**Inspired by:** [andreschaffer/event-sourcing-cqrs-examples](https://github.com/andreschaffer/event-sourcing-cqrs-examples) (Java)

A minimalistic bank domain demonstrating event sourcing with Wolverine's store-agnostic aggregate handlers. Not a code port -- built from scratch using the Java project's domain concepts.

**It runs on two event stores from the same handlers.** `Program.cs` is the only file that names a store: `EventStore=Marten` (the default -- Postgres, `ConnectionStrings:Marten`) or `EventStore=Fisher` (an embedded SQLite file, `ConnectionStrings:Fisher`, no docker). Everything else is written against the vocabulary Wolverine and JasperFx.Events share across Marten, Polecat and Fisher -- `[DeciderFunction]`, `[Entity]`, `Storage.StartStream()`, `IEventStoreOperations`, `IDocumentReadOperations`, and `Snapshot<T>` projections. See `docs/sample-wiring.md` footgun 14 for the swap table, and CLAUDE.md "Bobcat.CritterStack is store-agnostic" for why (issue #103).

## Domain

- **Client** -- enroll, update profile (event-sourced)
- **Account** -- open, deposit, withdraw (event-sourced, with balance guard)
- **Transaction History** -- inline projection built from deposit/withdrawal events

## Patterns Demonstrated

### Decider Function Workflow (`[DeciderFunction]`)

Deposit and withdrawal operations use Wolverine's store-agnostic `[DeciderFunction]` attribute (`Wolverine.Persistence.EventSourcing`; `Wolverine.Marten`'s `[AggregateHandler]` derives from it). Wolverine loads the Account aggregate from the event stream of whichever store is registered, passes it to the handler, appends the returned event, and commits -- all in one transaction.

```csharp
[WolverinePost("/api/accounts/{accountId}/deposits")]
[DeciderFunction]
public static (IResult, FundsDeposited) Post(DepositFunds command, Account account)
{
    var newBalance = account.Balance + command.Amount;
    return (Results.NoContent(), new FundsDeposited(command.AccountId, command.Amount, newBalance));
}
```

### Starting a stream without naming the store

`EnrollClient` and `OpenAccount` return a `Storage.StartStream<T>(id, events)` side effect beside their `Created<T>` response. The endpoint stays a pure function; Wolverine appends through the registered event store in the same transaction. (`IEventStoreOperations` is the injectable alternative when a handler needs more than a start or an append.)

### Validate Against Aggregate State

Withdrawal validates against the loaded aggregate's balance using a separate `Validate` method (Railway Programming pattern):

```csharp
public static ProblemDetails Validate(WithdrawFunds command, Account account)
{
    if (account.Balance < command.Amount)
        return new ProblemDetails { Detail = "Insufficient funds", Status = 400 };
    return WolverineContinue.NoProblems;
}
```

### Inline Snapshot Projections

Account, Client **and the `AccountTransactions` read model** use `SnapshotLifecycle.Inline` -- the snapshot document is always up-to-date after each event append. `Snapshot<T>` is the one projection shape every store spells identically, which is why the transaction history is a self-aggregating document (`Create(AccountOpened)`, `Apply(IEvent<FundsDeposited>)`, …) rather than a `SingleStreamProjection<,>` subclass -- that base class is Marten's, and Fisher and Polecat each have their own.

### Store-agnostic reads

The query endpoints take `IDocumentReadOperations` (documents: `LoadAsync<T>`, `Query<T>()` + `JasperFx.Events.Documents.ToListAsync`) and `IEventStoreOperations` (`AggregateStreamAsync<T>`), which Wolverine resolves to the current session on Marten, Polecat and Fisher alike.

### Entity Loading with Batch Query

`OpenAccount` uses `[Entity]` to load the Client by `ClientId` -- verifying the client exists before opening an account.

The spec arranges that Client with `GivenContext<Client>(…)`, not `GivenEvents<Client>(…)` (bobcat#502). At run time the two are the same append; on the Event Model, `GivenContext` puts Client in the OpenAccount slice's `ReadsFrom` -- a lookup the slice reads before deciding -- instead of claiming it as an aggregate. The stream OpenAccount decides against is the Account it starts. `EventModel.cs` pins it.

## Running

On Marten (the default) it needs the samples' Postgres -- `cd samples && docker compose up -d`. On Fisher it needs nothing.

```bash
dotnet run                                                  # Marten
EventStore=Fisher dotnet run                                # Fisher, bank_account.db next to appsettings.json

dotnet test --project Tests                              # the Bobcat specs, on Marten
EventStore=Fisher dotnet test --project Tests            # the same specs, on Fisher
BOBCAT_SPEC_CONSOLE=1 dotnet run --project Tests            # ...and print every spec as it rendered
```

**The specs run one at a time, on purpose** (`Tests/AssemblyAttributes.cs`). Every act here is Bobcat's store-only act, which reads back what the store gained while it ran and attributes it by stream (bobcat#503): events another test appends to its own streams are set aside as `LastAct.ForeignEvents`, and a failing `ThenEvents` says "cross-talk suspected" when there were any. That narrows the problem without closing it -- a stream another test *starts* at the same moment still looks like one this act started, and every spec here starts one (`EnrollClient`, `OpenAccount`). So whole-store assertions on a shared store keep parallelization off. For real parallelism, act through Wolverine's tracked session with WolverineFx.Bobcat's `WolverineScenario`, or give each test its own store or tenant.

Fisher builds its schema lazily, so the host calls `ApplyAllDatabaseChangesOnStartup()` -- without it the first append on a fresh file fails with `no such table: fi_streams` (footgun 13).

**Polecat (SQL Server) is the documented manual path, not a wired one.** It would be a third `case` in `Program.cs` of the same shape -- `AddPolecat(opts => { opts.Connection(...); opts.Projections.Snapshot<...>(SnapshotLifecycle.Inline); }).IntegrateWithWolverine()` from `WolverineFx.Polecat` -- and nothing outside `Program.cs` would change, because Polecat registers `IEventStore`, implements the same `IDocumentReadOperations` / `IEventStoreOperations` contracts, and `Wolverine.Polecat`'s `[AggregateHandler]` derives from the same `[DeciderFunction]`. It is not in this repo's `Program.cs` because nothing here has run it against a SQL Server, and a branch nobody has run is footgun 4 waiting to happen.

Swagger UI at `/swagger`.

## Specifications

<!-- JEREMY -->
The specs in `Tests/` are Bobcat's projected (code-first) model: ordinary xUnit v3 tests that render as
Given/When/Then specifications and land on the Event Model. There are no `.feature` files anymore. Every
spec class derives from `BankAccountSpec`, which is Bobcat's store-only `CritterStackSpec` plus the two ways
this application is driven -- an HTTP request through Alba, or a message handed to the Wolverine bus -- so
the same specs run on Marten and on Fisher.

```csharp
[BobcatSpec(typeof(WithdrawFunds), Domain = "Banking")]
public async Task withdrawing_more_than_the_balance_leaves_the_account_untouched()
{
    var accountId = Guid.NewGuid();
    await GivenEvents<Account>(accountId,
        new AccountOpened(accountId, Guid.NewGuid(), "USD"),
        new FundsDeposited(accountId, 100, 100));

    await WhenPosted(new WithdrawFunds(accountId, 200), "/api/accounts/{accountId}/withdrawals", accountId);

    ThenTheResponseStatusIs(400);
    ThenNoEvents();
    await ThenAggregate<Account>(accountId, Specify<Account>().With(x => x.Balance, 100m));
}
```

Nothing in that test is written for the Event Model, and it still tells the model plenty. `[BobcatSpec]`
says which slice the test is evidence for. The roles come off the vocabulary the test calls, through
`[EventModelRole]`: `GivenEvents<Account>` makes `Account` the slice's aggregate, `WhenPosted` makes
`WithdrawFunds` its command (and, through `[EventModelTrigger]`, `POST /api/accounts/{accountId}/withdrawals`
its trigger), and `ThenEvents` in the happy-path spec names what it emits. `WhenPosted`, `WhenPut`,
`WhenReceived` and `WhenReading` are the sample's own methods in `BankAccountSpec.cs`, and they re-declare
those attributes, because the generator reads the method a test calls and never what that method calls.

`WhenReading<T>` declares a `GET` trigger beside a `ReadModel` role, which makes it a read act: a spec whose
act it is lands on a **Query** slice (bobcat#501). The slice reads `T` through `ReadsFrom`. Streams the spec
arranged with `GivenEvents` are setup feeding a projection, so they stamp nothing on the query. The
two GET specs bind the slices Wolverine derives from those endpoints, which it names for their routes.

| Spec class | What it covers |
|------------|----------------|
| `BankAccountEventSourcing` | the command slices over HTTP, plus the transaction-history and client-accounts queries |
| `AccountView` | the `Account` View slice: arranged events, no act, the read model they fold into |
| `FreezeAccountSpecs` | the one message-handled slice, deliberately silent about `AccountFlagged` |
| `EventModel` | the assembled Event Model itself: four sources, one model, one planted disagreement |

The `EventModel` specs are the interesting ones. They assemble the model from the Wolverine chains, the
store's projection registry, the overlay in `Program.cs` and this assembly's generated
`BobcatEventModelSource`, and assert how it folds -- which source won each role, that the store's `Account`
view and the spec's are one slice, and that the planted disagreement on `FreezeAccount` (bobcat#172)
surfaces as a hotspot instead of vanishing. Set `BOBCAT_EVENT_MODEL_EXPORT=<file>` to write the merged
model out as JSON.

The GET slices carry no disagreement: since [wolverine#4975](https://github.com/JasperFx/wolverine/issues/4975)
Wolverine derives a GET chain as a Query that reads its response type, which is what the specs say too. Before it,
both GET slices merged as a View with a Pattern `SourceDisagreement`, and the `EventModel` specs pinned that so the
fix would turn them red. It did when the sample moved into the Wolverine repository and ran against it.

## API

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/clients` | Enroll a new client |
| PUT | `/api/clients/{clientId}` | Update client profile |
| GET | `/api/clients/{id}` | Get client |
| POST | `/api/accounts` | Open account for a client |
| GET | `/api/accounts/{id}` | Get account |
| GET | `/api/clients/{clientId}/accounts` | Get all accounts for a client |
| POST | `/api/accounts/{accountId}/deposits` | Deposit funds |
| POST | `/api/accounts/{accountId}/withdrawals` | Withdraw funds |
| GET | `/api/accounts/{accountId}/transactions` | Transaction history |
