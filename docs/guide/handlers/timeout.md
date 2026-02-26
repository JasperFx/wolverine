# Message Timeouts

You don't want your Wolverine application to effectively become non-responsive by a handful of messages
that accidentally run in an infinite loop and therefore block all other message execution. To that end,
Wolverine let's you enforce configurable execution timeouts. Wolverine does this through the usage of
setting a timeout on a `CancellationToken` used within the message execution. To play nicely with this
timeout, you should take in `CancellationToken` in your asynchronous message handler methods and use that
within asynchronous method calls.

When a timeout occurs, a `TaskCanceledException` will be thrown.

To override the default message timeout of 60 seconds, use this syntax at bootstrapping time:

<!-- snippet: sample_set_default_timeout -->
<a id='snippet-sample_set_default_timeout'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts => { opts.DefaultExecutionTimeout = 1.Minutes(); }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/SlowTests/message_timeout_mechanics.cs#L20-L25' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_set_default_timeout' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To override the message timeout on a message type by message type basis, you can use the `[MessageTimeout]`
attribute as shown below:

<!-- snippet: sample_MessageTimeout_on_handler -->
<a id='snippet-sample_MessageTimeout_on_handler'></a>
```cs
[MessageTimeout(1)]
public async Task Handle(PotentiallySlowMessage message, CancellationToken cancellationToken)
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/SlowTests/message_timeout_mechanics.cs#L97-L102' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_MessageTimeout_on_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
