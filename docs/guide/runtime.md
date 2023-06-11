# Message Handling Runtime

Next, even though I said that Wolverine does not require an adapter interface in *your* code, Wolverine itself does actually need that for its own internal runtime pipeline. To that end
Wolverine uses [dynamically generated code](./codegen) to "weave" adapter code around your message handler code. It also weaves in the calls to any middleware applied to your system.
In ideal circumstances, Wolverine is able to completely remove the runtime usage of an IoC container for even better performance. The 
end result is a runtime pipeline that is able to accomplish its tasks with potentially much less overhead than comparable .NET frameworks that depend on adapter interfaces.


## How Wolverine Consumes Your Message Handlers

If you're worried about the performance implications of Wolverine calling into your code without any interfaces or base classes, nothing to worry about because Wolverine **does not use Reflection at runtime** to call your actions. Instead, Wolverine uses [runtime
code generation with Roslyn](https://jeremydmiller.com/2015/11/11/using-roslyn-for-runtime-code-generation-in-marten/) to write the "glue" code around your actions. Internally, Wolverine is generating a subclass of `MessageHandler` for each known message type:

<!-- snippet: sample_MessageHandler -->
<a id='snippet-sample_messagehandler'></a>
```cs
public interface IMessageHandler
{
    Type MessageType { get; }

    LogLevel ExecutionLogLevel { get; }
    Task HandleAsync(MessageContext context, CancellationToken cancellation);
}

public abstract class MessageHandler : IMessageHandler
{
    public HandlerChain? Chain { get; set; }

    public abstract Task HandleAsync(MessageContext context, CancellationToken cancellation);

    public Type MessageType => Chain!.MessageType;

    public LogLevel ExecutionLogLevel => Chain!.ExecutionLogLevel;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Runtime/Handlers/MessageHandler.cs#L5-L26' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_messagehandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## IoC Container Integration

::: info
Lamar started its life as "Blue Milk," and was originally built specifically to support the "Jasper" framework which was eventually renamed 
and rebooted as "Wolverine." Even though Lamar was released many years before Wolverine, it was always intended to help make Wolverine possible. 
:::

Wolverine is only able to use [Lamar](https://jasperfx.github.io/lamar) as its IoC container, and actually quietly registers Lamar with your .NET application within
any call to `UseWolverine()`. Wolverine actually uses Lamar's configuration model to help build out its dynamically generated code and can mostly go far enough to
recreate what would be Lamar's "instance plan" with plain old C# as a way of making the runtime operations a little bit leaner.



