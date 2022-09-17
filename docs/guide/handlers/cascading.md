# Cascading Messages

Many times during the processing of a message you will need to create and send out other messages. Maybe you need to respond back to the original sender with a reply,
maybe you need to trigger a subsequent action, or send out additional messages to start some kind of background processing. You can do that by just having
your handler class use the `IMessageContext` interface as shown in this sample:

<!-- snippet: sample_NoCascadingHandler -->
<a id='snippet-sample_nocascadinghandler'></a>
```cs
public class NoCascadingHandler
{
    private readonly IMessageContext _bus;

    public NoCascadingHandler(IMessageContext bus)
    {
        _bus = bus;
    }

    public void Consume(MyMessage message)
    {
        // do whatever work you need to for MyMessage,
        // then send out a new MyResponse
        _bus.SendAsync(new MyResponse());
    }
}
```
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/CascadingSamples.cs#L15-L32' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_nocascadinghandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The code above certainly works and this is consistent with most of the competing service bus tools. However, Wolverine supports the concept of _cascading messages_
that allow you to automatically send out objects returned from your handler methods without having to use `IMessageContext` as shown below:

<!-- snippet: sample_CascadingHandler -->
<a id='snippet-sample_cascadinghandler'></a>
```cs
public class CascadingHandler
{
    public MyResponse Consume(MyMessage message)
    {
        return new MyResponse();
    }
}
```
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/CascadingSamples.cs#L35-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_cascadinghandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When Wolverine executes `CascadingHandler.Consume(MyMessage)`, it "knows" that the `MyResponse` return value should be sent through the
service bus as part of the same transaction with whatever routing rules apply to `MyResponse`. A couple things to note here:

* Cascading messages returned from handler methods will not be sent out until after the original message succeeds and is part of the underlying
  transport transaction
* Null's returned by handler methods are simply ignored
* The cascading message feature was explicitly designed to make unit testing handler actions easier by shifting the test strategy
  to [state-based](http://blog.jayfields.com/2008/02/state-based-testing.html) where you mostly need to verify the state of the response
  objects instead of mock-heavy testing against calls to `IMessageContext`.

The response types of your message handlers can be:

1. A specific message type
1. `object`
1. The Wolverine `Envelope` if you need to customize how the cascading response is to be sent (schedule send, mark expiration times, route yourself, etc.)
1. `IEnumerable<object>` or `object[]` to make multiple responses
1. A [Tuple](https://docs.microsoft.com/en-us/dotnet/csharp/tuples) type to express the exact kinds of responses your message handler returns


## Request/Reply Scenarios

Normally, cascading messages are just sent out according to the configured subscription rules for that message type, but there's
an exception case. If the original sender requested a response, Wolverine will automatically send the cascading messages returned
from the action to the original sender if the cascading message type matches the reply that the sender had requested.
If you're examining the `Envelope` objects for the message, you'll see that the "reply-requested" header
is "MyResponse."

Let's say that we have two running service bus nodes named "Sender" and "Receiver." If this code below
is called from the "Sender" node:

<!-- snippet: sample_Request/Replay_with_cascading -->
<a id='snippet-sample_request/replay_with_cascading'></a>
```cs
public class Requester
{
    private readonly IMessageContext _bus;

    public Requester(IMessageContext bus)
    {
        _bus = bus;
    }

    public ValueTask GatherResponse()
    {
        return _bus.SendAsync(new MyMessage(), DeliveryOptions.RequireResponse<MyResponse>());
    }
}
```
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/CascadingSamples.cs#L46-L61' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_request/replay_with_cascading' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and inside Receiver we have this code:

<!-- snippet: sample_CascadingHandler -->
<a id='snippet-sample_cascadinghandler'></a>
```cs
public class CascadingHandler
{
    public MyResponse Consume(MyMessage message)
    {
        return new MyResponse();
    }
}
```
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/CascadingSamples.cs#L35-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_cascadinghandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Assuming that `MyMessage` is configured to be sent to "Receiver," the following steps take place:

1. Sender sends a `MyMessage` message to the Receiver node with the "reply-requested" header value of "MyResponse"
1. Receiver handles the `MyMessage` message by calling the `CascadingHandler.Consume(MyMessage)` method
1. Receiver sees the value of the "reply-requested" header matches the response, so it sends the `MyResponse` object back to Sender
1. When Sender receives the matching `MyResponse` message that corresponds to the original `MyMessage`, it sets the completion back
   to the Task returned by the `IMessageContext.Request<TResponse>()` method


## Conditional Responses

You may need some conditional logic within your handler to know what the cascading message is going to be. If you need to return
different types of cascading messages based on some kind of logic, you can still do that by making your handler method return signature
be `object` like this sample shown below:

<!-- snippet: sample_ConditionalResponseHandler -->
<a id='snippet-sample_conditionalresponsehandler'></a>
```cs
public class ConditionalResponseHandler
{
    public object Consume(DirectionRequest request)
    {
        switch (request.Direction)
        {
            case "North":
                return new GoNorth();
            case "South":
                return new GoSouth();
        }

        // This does nothing
        return null;
    }
}
```
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/CascadingSamples.cs#L72-L89' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conditionalresponsehandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Schedule Response Messages

You may want to raise a delayed or scheduled response. In this case you will need to return an <[linkto:documentation/integration/customizing_envelopes;title=Envelope]> for the response as shown below:

<!-- snippet: sample_DelayedResponseHandler -->
<a id='snippet-sample_delayedresponsehandler'></a>
```cs
public class ScheduledResponseHandler
{
    public Envelope Consume(DirectionRequest request)
    {
        return new Envelope(new GoWest()).ScheduleDelayed(TimeSpan.FromMinutes(5));
    }

    public Envelope Consume(MyMessage message)
    {
        // Process GoEast at 8 PM local time
        return new Envelope(new GoEast()).ScheduleAt(DateTime.Today.AddHours(20));
    }
}
```
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/CascadingSamples.cs#L94-L108' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_delayedresponsehandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Multiple Cascading Messages

You can also raise any number of cascading messages by returning either any type that can be
cast to `IEnumerable<object>`, and Wolverine will treat each element as a separate cascading message.
An empty enumerable is just ignored.

<!-- snippet: sample_MultipleResponseHandler -->
<a id='snippet-sample_multipleresponsehandler'></a>
```cs
public class MultipleResponseHandler
{
    public IEnumerable<object> Consume(MyMessage message)
    {
        // Go North now
        yield return new GoNorth();

        // Go West in an hour
        yield return new Envelope(new GoWest()).ScheduleDelayed(TimeSpan.FromHours(1));
    }
}
```
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/CascadingSamples.cs#L111-L123' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_multipleresponsehandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Using C# Tuples as Return Values

Sometimes you may well need to return multiple cascading messages from your original message action. In FubuMVC, Wolverine's forebear, you had to return either `object[]` or `IEnumerable<object>` as the return type of your action -- which had the unfortunate side effect of partially obfuscating your code by making it less clear what message types were being cascaded from your handler without carefully
reading the message body. In Wolverine, we still support the "mystery meat" `object` return value signatures, but now you can also use
C# tuples to better denote the cascading message types.

This handler cascading a pair of messages:

<!-- snippet: sample_MultipleResponseHandler -->
<a id='snippet-sample_multipleresponsehandler'></a>
```cs
public class MultipleResponseHandler
{
    public IEnumerable<object> Consume(MyMessage message)
    {
        // Go North now
        yield return new GoNorth();

        // Go West in an hour
        yield return new Envelope(new GoWest()).ScheduleDelayed(TimeSpan.FromHours(1));
    }
}
```
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/CascadingSamples.cs#L111-L123' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_multipleresponsehandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

can be rewritten with C# 7 tuples to:

<!-- snippet: sample_TupleResponseHandler -->
<a id='snippet-sample_tupleresponsehandler'></a>
```cs
public class TupleResponseHandler
{
    // Both GoNorth and GoWest will be interpreted as
    // cascading messages
    public (GoNorth, GoWest) Consume(MyMessage message)
    {
        return (new GoNorth(), new GoWest());
    }
}
```
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/CascadingSamples.cs#L125-L135' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_tupleresponsehandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The sample above still treats both `GoNorth` and the `ScheduledResponse` as cascading messages. The Wolverine team thinks that the
tuple-ized signature makes the code more self-documenting and easier to unit test.
