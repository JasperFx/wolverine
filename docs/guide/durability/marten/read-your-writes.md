# Read Your Writes <Badge type="tip" text="7.0" />

An asynchronous projection is updated by the async daemon a little while after your events are committed. If a client
posts a command and then immediately reads that projection, it can easily see the data from before the command.
Inline projections don't have this problem, but you don't always want an inline projection.

Wolverine's answer is the `[WaitForProjection<T>]` attribute on a message handler or an HTTP endpoint. After the
handler or endpoint commits, Wolverine waits until the projection `T` has processed the events that were just appended
before the message is finished or the HTTP response is written. `T` can be the projection class or the document type
it writes:

<!-- snippet: sample_wait_for_projection_on_an_http_endpoint -->
<a id='snippet-sample_wait_for_projection_on_an_http_endpoint'></a>
```cs
[WolverinePost("/ryw/points")]
[WaitForProjection<RywPointsView>]
public static void Post(RywAddPoints command, IDocumentSession session)
{
    if (command.Points == 0) return;
    session.Events.Append(command.Id, new RywPointsAdded(command.Points));
}

// With a Wolverine-Consistency-Token on the request, this waits for that position before it loads the view
[WolverineGet("/ryw/points/{id}")]
[WaitForProjection<RywPointsView>]
public static Task<RywPointsView?> Get(Guid id, IQuerySession session)
    => session.LoadAsync<RywPointsView>(id);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Marten/read_your_writes_with_wait_for_projection.cs#L345-L361' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wait_for_projection_on_an_http_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And the same thing in a message handler:

<!-- snippet: sample_wait_for_projection_in_a_message_handler -->
<a id='snippet-sample_wait_for_projection_in_a_message_handler'></a>
```cs
[WaitForProjection<RywPointsView>]
public static void Handle(RywAddPointsMessage command, IDocumentSession session)
{
    session.Events.Append(command.Id, new RywPointsAdded(command.Points));
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Marten/read_your_writes_with_wait_for_projection.cs#L377-L385' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wait_for_projection_in_a_message_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You can use the attribute more than once to wait on several projections.

## The Consistency Token

An HTTP endpoint with `[WaitForProjection<T>]` returns the position of the events it appended in the
`Wolverine-Consistency-Token` response header. Going the other way, when a request to an endpoint with
`[WaitForProjection<T>]` carries a `Wolverine-Consistency-Token` header, Wolverine waits for the projection to reach that
position *before* the endpoint runs. That's what the `GET` endpoint in the sample above is doing, so a client can take
the token from a `POST` and send it on the next `GET`.

A message handler with `[WaitForProjection<T>]` does the same with a `Wolverine-Consistency-Token` header on the
incoming message:

```cs
var options = new DeliveryOptions();
options.Headers[ReadYourWrites.ConsistencyTokenHeader] = token;

var view = await bus.InvokeAsync<PointsView>(new ReadPoints(id), options);
```

The token is the `EventStorePosition` from JasperFx.Events, written out with its `ToString()`. If you are writing
through the event store yourself, you can get the same position from the session after `SaveChangesAsync()` with
`((IStorageOperations)session).LastAppendedPosition`. A header value that isn't a position is logged and ignored.

## Timeouts

The default timeout is 5 seconds, and you can change it with `[WaitForProjection<T>(TimeoutMs = 1000)]`.

A wait that runs out never fails the message or the request. Wolverine logs a warning and carries on, and an HTTP
endpoint adds `Wolverine-Projection-Timeout: true` to the response. The same happens right away if the projection is
paused or being rebuilt. Wolverine only sees a projection paused on *another* node if extended progression tracking is
turned on in the event store; otherwise the wait runs to its timeout.

## Notes

* This works with Marten, Polecat and Fisher, and with ancillary stores.
* The wait reads the event store's progression table, so it works no matter which node is running the projection.
* An inline or live projection is already up to date when the commit returns, so there's nothing to wait for. That
  also means you can use the same attribute when a projection is inline in tests and asynchronous in production.
* Wolverine only waits for events the handler or endpoint actually committed through Wolverine's transactional
  middleware, like `[WriteAggregate]` or `AutoApplyTransactions()`. If nothing was appended, there's no wait and no token.
* F# can't apply a generic attribute, so you can't write `[WaitForProjection<T>]` on an F# handler or endpoint. From F#,
  apply it in a policy instead: an `IHandlerPolicy` for message handlers or an `IHttpPolicy` for HTTP endpoints that calls
  `WaitForProjectionAttribute<T>().Modify(chain, rules, container)` on the chains you want. The generated F# code
  works the same way.

## Testing with Bobcat

In a Bobcat specification, `WhenPosted` against an endpoint marked this way returns after the projection has the
events, so the next step can read them:

<!-- snippet: sample_spec_against_an_endpoint_with_wait_for_projection -->
<a id='snippet-sample_spec_against_an_endpoint_with_wait_for_projection'></a>
```cs
[Fact]
public async Task points_are_in_the_balance_once_the_post_returns()
{
    var account = Guid.NewGuid();

    await WhenPosted(new AddLoyaltyPoints(account, 25));

    await ThenReadModel<LoyaltyBalance>(account, Specify<LoyaltyBalance>().With(x => x.Points, 25));
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Bobcat/Wolverine.Bobcat.Http.Tests/read_your_writes.cs#L21-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_spec_against_an_endpoint_with_wait_for_projection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
