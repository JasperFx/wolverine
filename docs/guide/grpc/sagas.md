# Integration with Sagas

gRPC services can start and continue [Wolverine sagas](/guide/durability/sagas), and doing so takes
no gRPC-specific code at all: the service shim forwards the request to `IMessageBus.InvokeAsync<T>`,
and the saga on the other side is an ordinary Wolverine saga handler. Everything saga-related —
identity resolution, persistence, `MarkCompleted()` — happens in the same handler pipeline an HTTP
endpoint or a message listener would use.

There is one difference from [Wolverine.HTTP's saga integration](/guide/http/sagas) worth
understanding up front. HTTP endpoints can *create* a saga by returning a `Saga`-derived value from
the endpoint method, because Wolverine generates the endpoint chain itself and applies return-value
mechanics to it. A gRPC service method is a thin shim in front of `Bus.InvokeAsync<T>` — the chain
that runs is the *handler's* chain, so the saga is started the way any message starts a saga: by a
`Start`/`Starts` method on the saga type, with the saga identity resolved **from the message body**.

## An example saga

Let's adapt the same reservation example the HTTP documentation uses. The saga is started by a
`StartReservationRequest` and continued (and completed) by a `BookReservationRequest`:

<!-- snippet: sample_grpc_reservation_saga -->
<a id='snippet-sample_grpc_reservation_saga'></a>
```cs
public class ReservationSaga : Saga
{
    public string Id { get; set; } = string.Empty;
    public bool Booked { get; set; }

    // Starts the saga. The saga id comes off the message body
    // (StartReservationRequest.ReservationId), so no envelope header
    // is needed for this to work over a gRPC hop
    public ReservationBookedReply Start(StartReservationRequest start)
    {
        Id = start.ReservationId!;
        return new ReservationBookedReply { ReservationId = start.ReservationId };
    }

    public BookReservationReply Handle(BookReservationRequest book)
    {
        Booked = true;

        // That's it, we're done. Delete the saga state after the message is done.
        MarkCompleted();

        return new BookReservationReply { Completed = true };
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine.Grpc.Tests/SagaOverGrpc/ReservationSaga.cs#L10-L37' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_grpc_reservation_saga' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that there is nothing gRPC-flavored here — this is exactly the saga you would write for
messaging or HTTP usage.

## The contracts

The gRPC request DTOs double as the saga messages. The important detail is that each message
**carries the saga identity on the body**, using Wolverine's standard saga identity conventions
(`Id`, `[SagaTypeName]Id`, or a `[SagaIdentity]`-decorated member):

<!-- snippet: sample_grpc_saga_contracts -->
<a id='snippet-sample_grpc_saga_contracts'></a>
```cs
[ServiceContract]
public interface IReservationSagaService
{
    Task<ReservationBookedReply> Start(StartReservationRequest request, CallContext context = default);
    Task<BookReservationReply> Book(BookReservationRequest request, CallContext context = default);
}

[ProtoContract]
public class StartReservationRequest
{
    // "ReservationId" matches the ReservationSaga type name minus the "Saga"
    // suffix, so Wolverine resolves the saga identity from this member
    [ProtoMember(1)]
    public string? ReservationId { get; set; }
}

[ProtoContract]
public class ReservationBookedReply
{
    [ProtoMember(1)]
    public string? ReservationId { get; set; }
}

[ProtoContract]
public class BookReservationRequest
{
    // "Id" is also matched as the saga identity by convention
    [ProtoMember(1)]
    public string? Id { get; set; }
}

[ProtoContract]
public class BookReservationReply
{
    [ProtoMember(1)]
    public bool Completed { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine.Grpc.Tests/SagaOverGrpc/ReservationSagaContracts.cs#L15-L55' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_grpc_saga_contracts' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## The service shim

The service class just forwards to the bus, exactly like every other Wolverine gRPC service:

<!-- snippet: sample_grpc_saga_service -->
<a id='snippet-sample_grpc_saga_service'></a>
```cs
public class ReservationSagaGrpcService : WolverineGrpcServiceBase, IReservationSagaService
{
    public ReservationSagaGrpcService(IMessageBus bus) : base(bus)
    {
    }

    // Nothing here is saga-aware -- the saga mechanics all happen
    // in the Wolverine handler pipeline behind InvokeAsync()
    public Task<ReservationBookedReply> Start(StartReservationRequest request, CallContext context = default)
        => Bus.InvokeAsync<ReservationBookedReply>(request, context.CancellationToken);

    public Task<BookReservationReply> Book(BookReservationRequest request, CallContext context = default)
        => Bus.InvokeAsync<BookReservationReply>(request, context.CancellationToken);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine.Grpc.Tests/SagaOverGrpc/ReservationSagaGrpcService.cs#L12-L29' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_grpc_saga_service' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Putting it together

Starting the saga, observing its persisted state, continuing it, and completing it — all through
gRPC calls:

<!-- snippet: sample_starting_and_continuing_saga_over_grpc -->
<a id='snippet-sample_starting_and_continuing_saga_over_grpc'></a>
```cs
[Fact]
public async Task can_start_and_continue_a_message_identified_saga_over_grpc()
{
    var client = _fixture.CreateReservationClient();
    var persistor = _fixture.Services.GetRequiredService<InMemorySagaPersistor>();

    // Start the saga over gRPC — same InvokeAsync path a WolverinePost endpoint would take.
    var booked = await client.Start(new StartReservationRequest { ReservationId = "dinner" });
    booked.ReservationId.ShouldBe("dinner");

    // The saga was persisted by its message-supplied id, no saga-id header required.
    var saved = persistor.Load<ReservationSaga>("dinner");
    saved.ShouldNotBeNull();
    saved.Booked.ShouldBeFalse();

    // Continue the saga over gRPC — id comes off the follow-up message.
    var result = await client.Book(new BookReservationRequest { Id = "dinner" });
    result.Completed.ShouldBeTrue();

    // Handle(BookReservationRequest) marked the saga completed, so its state is deleted.
    persistor.Load<ReservationSaga>("dinner").ShouldBeNull();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine.Grpc.Tests/SagaOverGrpc/saga_over_grpc_tests.cs#L35-L60' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_starting_and_continuing_saga_over_grpc' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The example above uses Wolverine's default in-memory saga persistence for clarity. With
[Marten](/guide/durability/marten/sagas), [EF Core](/guide/durability/efcore/), or
[RavenDb](/guide/durability/ravendb) saga persistence configured, the same flow persists the saga
state durably with no change to the saga, the contracts, or the service shim.

## Saga identity must ride on the message

Wolverine has two ways to resolve which saga state a message belongs to:

1. **Message-identified** — the identity is a member on the message body (`Id`,
   `ReservationId` for a `ReservationSaga`, or any member marked with `[SagaIdentity]`). This is
   what the samples above use, and it works over gRPC exactly like it works over HTTP and messaging.
2. **Header-identified** — the message carries no identity member, and Wolverine falls back to the
   envelope's `saga-id` header.

**Only message-identified sagas are supported over a gRPC service hop.** The `saga-id` envelope
header does not cross the hop — neither the client nor server propagation interceptor carries it —
so a header-identified saga invoked through a gRPC service cannot resolve an id at all.

As of 6.18.0 that fails with an explicit diagnostic rather than an opaque one: the caller gets a
`StatusCode.InvalidArgument` `RpcException` whose detail names both the cause and the fix.

> Could not determine a saga id for this request. A saga started or continued over a gRPC hop must
> carry its identity ON THE MESSAGE BODY: the 'saga-id' envelope header is not propagated across a
> gRPC call, so a header-identified saga cannot work over gRPC. Put the saga identity on the request
> message itself (a property Wolverine can match to the saga id, or one marked with
> `[SagaIdentity]`).

`InvalidArgument` rather than `Internal` is deliberate ([AIP-193](https://google.aip.dev/193)): the
request cannot succeed as sent, and no amount of retrying will change that — it is a contract
problem, not a transient server fault.

The practical guidance is simple, and is the better design anyway:

::: tip
Put the saga identity on the request DTO. It makes the contract self-describing for non-Wolverine
gRPC clients too — a Go or Java caller can start or continue the saga without knowing anything
about Wolverine envelope headers.
:::

## What about cascading messages?

Because the saga runs in the ordinary handler pipeline, all the usual saga behaviors work unchanged
over a gRPC hop: additional return values become [cascading messages](/guide/handlers/cascading),
[saga timeouts](/guide/durability/sagas.html#timeout-messages) can be scheduled, and
`MarkCompleted()` deletes the saga state when the work is done. The only rule specific to gRPC is
the one shown above: when the service method uses `Bus.InvokeAsync<TResponse>(request)`, the
handler's **response value** is what travels back to the RPC caller, and other return values cascade
as messages.
