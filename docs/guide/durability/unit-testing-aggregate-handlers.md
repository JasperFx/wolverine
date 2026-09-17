# Unit Testing Aggregate Handlers

::: tip
Everything on this page works exactly the same against Marten, Polecat, or Fisher. `IEventStream<T>` lives in
`JasperFx.Events`, so there is nothing store-specific about the handler or its test.
:::

Let's say you're using the [aggregate handler workflow](/guide/durability/marten/event-sourcing) and you've got a
handler that takes the event stream itself:

<!-- snippet: sample_stub_event_stream_handler -->
<a id='snippet-sample_stub_event_stream_handler'></a>
```cs
public record Account(decimal Balance, bool IsFrozen);

public record Withdraw(Guid AccountId, decimal Amount);

public record FundsWithdrawn(decimal Amount);

public record WithdrawalRejected(string Reason);

public static class WithdrawHandler
{
    public static void Handle(Withdraw command, [WriteModel] IEventStream<Account> stream)
    {
        var account = stream.Aggregate;

        if (account is null || account.IsFrozen)
        {
            stream.AppendOne(new WithdrawalRejected("Account is not available"));
            return;
        }

        if (account.Balance < command.Amount)
        {
            stream.AppendOne(new WithdrawalRejected("Insufficient funds"));
            return;
        }

        stream.AppendOne(new FundsWithdrawn(command.Amount));
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/unit_testing_aggregate_handlers.cs#L8-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stub_event_stream_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

There's a nice property hiding in that signature. The handler doesn't know anything about a database. It reads
`stream.Aggregate`, makes a decision, and appends events. Given the aggregate state, the events it appends are the
entire observable result -- which means you can test the interesting part without a host, a database, or a
container.

`StubEventStream<T>` is the stand-in. Hand it the aggregate state the handler should see, call `Handle` directly,
and assert on what came out:

<!-- snippet: sample_stub_event_stream_happy_path -->
<a id='snippet-sample_stub_event_stream_happy_path'></a>
```cs
[Fact]
public void withdraws_when_the_funds_are_there()
{
    // Arrange -- the aggregate state the handler should see. No host, no database, no mocks.
    var stream = new StubEventStream<Account>(new Account(500m, false));

    // Act -- just call the handler. It is a static method taking the stream.
    WithdrawHandler.Handle(new Withdraw(stream.Id, 100m), stream);

    // Assert on the events the handler decided to append
    stream.EventsAppended.Single()
        .ShouldBeOfType<FundsWithdrawn>()
        .Amount.ShouldBe(100m);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/unit_testing_aggregate_handlers.cs#L44-L61' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stub_event_stream_happy_path' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

That's the whole pattern. `EventsAppended` is every event the handler emitted, in order, whichever of `AppendOne` or
`AppendMany` it used.

::: info
`[WriteModel]` is the store-agnostic spelling of the aggregate handler workflow, and is what you want in new code.
`[WriteAggregate]` is the older Marten spelling and still works -- Polecat and Fisher ship the same attribute name.
The unit test doesn't care either way, because you're calling the handler yourself rather than letting Wolverine
resolve the parameter.
:::

## Don't reach for a mocking library here

I'd honestly rather you didn't mock `IEventStream<T>`. You certainly *can*, but look at what the assertion actually
proves:

```csharp
// Please don't
var stream = Substitute.For<IEventStream<Account>>();
stream.Aggregate.Returns(new Account(500m, false));

WithdrawHandler.Handle(new Withdraw(Guid.NewGuid(), 100m), stream);

stream.Received(1).AppendOne(Arg.Any<FundsWithdrawn>());
```

That says a method got called with *something* of the right type. It says nothing about the amount, which is the
entire content of the handler's decision -- and `Arg.Any<FundsWithdrawn>()` will happily pass if the handler
withdraws the wrong number, or appends a second event you didn't expect. The recorded list is both stronger and
easier to read.

## A stream that doesn't exist yet

A null aggregate means the stream hasn't been started. That's what a handler sees for a command against an id with
no events behind it, so it's worth covering:

<!-- snippet: sample_stub_event_stream_missing_stream -->
<a id='snippet-sample_stub_event_stream_missing_stream'></a>
```cs
[Fact]
public void rejects_the_withdrawal_when_the_stream_does_not_exist_yet()
{
    // A null aggregate is a stream that has not been started
    var stream = new StubEventStream<Account>(null);

    WithdrawHandler.Handle(new Withdraw(stream.Id, 100m), stream);

    stream.EventsAppended.Single()
        .ShouldBeOfType<WithdrawalRejected>()
        .Reason.ShouldBe("Account is not available");
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/unit_testing_aggregate_handlers.cs#L63-L78' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stub_event_stream_missing_stream' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Versions, ids, and keys

`Id` and `Key` are both populated with fresh values out of the box, because the stub has no way of knowing which
identity style your handler reads. Set whichever one it actually uses -- that's how you wire a command's id to a
particular stream when a handler works against more than one -- see "Targeting Multiple Streams at Once" on the
[aggregate handler page](/guide/durability/marten/event-sourcing).

`StartingVersion` and `CurrentVersion` are settable for a handler that branches on version:

<!-- snippet: sample_stub_event_stream_versions -->
<a id='snippet-sample_stub_event_stream_versions'></a>
```cs
[Fact]
public void the_versions_are_settable_for_a_version_sensitive_handler()
{
    var stream = new StubEventStream<Account>(new Account(500m, false))
    {
        StartingVersion = 4,
        CurrentVersion = 4
    };

    WithdrawHandler.Handle(new Withdraw(stream.Id, 100m), stream);

    // The stub records; it does not advance the version the way a real
    // store would at save time
    stream.CurrentVersion.ShouldBe(4);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/unit_testing_aggregate_handlers.cs#L92-L110' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stub_event_stream_versions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Where this stops

The stub records. It does not persist, project, validate, or advance the stream version, and it will never throw a
`ConcurrencyException` at you. That's deliberate -- it keeps the test about the handler's decision rather than about
the store's behavior.

The flip side is that optimistic concurrency is not testable this way. If you need to prove that two concurrent
commands against one stream behave correctly, that's an integration test against a real store, and
[Wolverine's integration testing support](/guide/testing) is where to look.

::: tip
The JasperFx documentation has [its own page on this pattern](https://shared-libs.jasperfx.net/events/unit-testing-handlers)
with more detail on `EventsAppended` versus the `Events` envelopes, and on the event registry overload.
:::
