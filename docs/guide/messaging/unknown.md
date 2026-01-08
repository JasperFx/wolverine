# Unknown Messages

When Wolverine receives a message from the outside world, it's keying off the [message type name](/guide/messages.html#message-type-name-or-alias) from the `Envelope` to 
"know" what message type it's receiving and therefore, which handler(s) to execute. It's an imperfect world of course,
so it's perfectly possible that your system will receive a message from the outside world with a message type name that
your system does not recognize.

Out of the box Wolverine will simply log that it received an unknown message type and discard the message, but there are
means to take additional actions on "missing handler" messages where Wolverine does not recognize the message type.

## Move to the Dead Letter Queue <Badge type="tip" text="5.3" />

You can declaratively tell Wolverine to persist every message received with an unknown message type name
to the dead letter queue with this flag:

<!-- snippet: sample_unknown_messages_go_to_dead_letter_queue -->
<a id='snippet-sample_unknown_messages_go_to_dead_letter_queue'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("rabbit");
    opts.UseRabbitMq(connectionString).UseConventionalRouting();

    // All unknown message types received should be placed into 
    // the proper dead letter queue mechanism
    opts.UnknownMessageBehavior = UnknownMessageBehavior.DeadLetterQueue;
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/moving_unknown_message_type_to_dlq.cs#L23-L36' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_unknown_messages_go_to_dead_letter_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The message will be moved to the dead letter queue mechanism for the listening endpoint where the message was received.

## Custom Actions

::: note
The missing handlers are additive, meaning that you can provide more than one and Wolverine will try to execute
each one that is registered for the missing handler behavior. 
:::

You can direct Wolverine to take custom actions on messages received with unknown message type names by providing
a custom implementation of this interface:

<!-- snippet: sample_IMissingHandler -->
<a id='snippet-sample_IMissingHandler'></a>
```cs
namespace Wolverine;

/// <summary>
///     Hook interface to receive notifications of envelopes received
///     that do not match any known handlers within the system
/// </summary>
public interface IMissingHandler
{
    /// <summary>
    ///     Executes for unhandled envelopes
    /// </summary>
    /// <param name="context"></param>
    /// <param name="root"></param>
    /// <returns></returns>
    ValueTask HandleAsync(IEnvelopeLifecycle context, IWolverineRuntime root);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/IMissingHandler.cs#L4-L22' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_IMissingHandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Here's a made up sample that theoretically posts a message to a Slack room by sending a Wolverine message in response:

<!-- snippet: sample_MyCustomActionForMissingHandlers -->
<a id='snippet-sample_MyCustomActionForMissingHandlers'></a>
```cs
public class MyCustomActionForMissingHandlers : IMissingHandler
{
    public ValueTask HandleAsync(IEnvelopeLifecycle context, IWolverineRuntime root)
    {
        var bus = new MessageBus(root);
        return bus.PublishAsync(new PostInSlack("Incidents",
            $"Got an unknown message with type '{context.Envelope.MessageType}' and id {context.Envelope.Id}"));
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MissingHandlerSample.cs#L10-L22' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_MyCustomActionForMissingHandlers' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And simply registering that with your application's IoC container against the `IMissingHandler` interface like this:

<!-- snippet: sample_registering_custom_missing_handler -->
<a id='snippet-sample_registering_custom_missing_handler'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // configuration
    opts.UnknownMessageBehavior = UnknownMessageBehavior.DeadLetterQueue;
});

builder.Services.AddSingleton<IMissingHandler, MyCustomActionForMissingHandlers>();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MissingHandlerSample.cs#L28-L39' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_custom_missing_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Tracked Session Testing

Just know that the [Tracked Session](/guide/testing.html#integration-testing-with-tracked-sessions) subsystem for integration
testing exposes a separate record collection for `NoHandlers` and reports when that happens through its output for hopefully
easy troubleshooting on test failures.

