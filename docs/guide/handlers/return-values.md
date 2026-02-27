# Return Values

The valid return values for Wolverine handlers are:

| Return Type                      | Description                                                                                      |
|----------------------------------|--------------------------------------------------------------------------------------------------|
| `void`                           | Synchronous methods because hey, who wants to litter their code with `Task.CompletedTask` every which way? |
| `Task`                           | If you need to do asynchronous work                                                              |
| `ValueTask`                      | If you need to *maybe* do asynchronous work with other people's APIs                             |
| `IEnumerable<object>`            | Published 0 to many cascading messages                                                           |
| `IAsyncEnumerable<object>`       | Asynchronous method that will lead to 0 to many cascading messages                               |
| Implements `ISideEffect`         | See [Side Effects](/guide/handlers/side-effects) for more information                            |
| `OutgoingMessages`               | Special collection type that is treated as [cascading messages](/guide/handlers/cascading)       |
| Inherits from `Saga` | Creates a new [stateful saga](/guide/durability/sagas)                                           |
| *Your message type*              | By returning another type, Wolverine treats the return value as "cascaded" message to publish    |


In all cases up above, if the endpoint method is asynchronous using either `Task<T>` or `ValueTask<T>`, the `T` is the
return value, with the same behavior as the synchronous `T` would have.

Wolverine also supports [Tuple](https://learn.microsoft.com/en-us/dotnet/api/system.tuple?view=net-7.0) responses, in which case
every single item in a tuple `(T, T1, T2)` is an individual return value that Wolverine treats independently. Here's
an example from the saga support of message handler that returns both a new `OrderSaga` to be persisted and a separate
`OrderTimeout` to be published as a cascaded message:

<!-- snippet: sample_starting_a_saga_inside_a_handler -->
<a id='snippet-sample_starting_a_saga_inside_a_handler'></a>
```cs
// This method would be called when a StartOrder message arrives
// to start a new Order
public static (Order, OrderTimeout) Start(StartOrder order, ILogger<Order> logger)
{
    logger.LogInformation("Got a new order with id {Id}", order.OrderId);

    // creating a timeout message for the saga
    return (new Order{Id = order.OrderId}, new OrderTimeout(order.OrderId));
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderSagaSample/OrderSaga.cs#L24-L36' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_starting_a_saga_inside_a_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Custom Return Value Handling

It's actually possible to create custom conventions in Wolverine for how different return types are utilized in the generated
code Wolverine wraps around message handler methods. 

::: info
You can achieve exactly what this sample demonstrates by just implementing the `ISideEffect` interface from `WriteFile`
without having to write your own policy or even having to know very much about Wolverine internals to accomplish the
isolation of the file writing side effect.
:::

For an example, let's say that you want to isolate the [side effect](https://en.wikipedia.org/wiki/Side_effect_(computer_science)) of writing out file contents from your handler
methods by returning a custom return value called `WriteFile`:

<!-- snippet: sample_WriteFile -->
<a id='snippet-sample_WriteFile'></a>
```cs
// This has to be public btw
public record WriteFile(string Path, string Contents)
{
    public Task WriteAsync()
    {
        return File.WriteAllTextAsync(Path, Contents);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CustomReturnType.cs#L13-L24' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_WriteFile' title='Start of snippet'>anchor</a></sup>
<a id='snippet-sample_WriteFile-1'></a>
```cs
// ISideEffect is a Wolverine marker interface
public class WriteFile : ISideEffect
{
    public string Path { get; }
    public string Contents { get; }

    public WriteFile(string path, string contents)
    {
        Path = path;
        Contents = contents;
    }

    // Wolverine will call this method.
    public Task ExecuteAsync(PathSettings settings)
    {
        if (!Directory.Exists(settings.Directory))
        {
            Directory.CreateDirectory(settings.Directory);
        }

        return File.WriteAllTextAsync(Path, Contents);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/using_custom_side_effect.cs#L43-L69' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_WriteFile-1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And now, let's teach Wolverine to call the `WriteAsync()` method on each `WriteFile` that is returned from a message handler
at runtime instead of Wolverine using the default policy of treating it as a cascaded message. To do that, I'm going
to write a custom `IChainPolicy` like so:

<!-- snippet: sample_WriteFilePolicy -->
<a id='snippet-sample_WriteFilePolicy'></a>
```cs
internal class WriteFilePolicy : IChainPolicy
{
    // IChain is a Wolverine model to configure the code generation of
    // a message or HTTP handler and the core model for the application
    // of middleware
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        var method = ReflectionHelper.GetMethod<WriteFile>(x => x.WriteAsync());

        // Check out every message and/or http handler:
        foreach (var chain in chains)
        {
            var writeFiles = chain.ReturnVariablesOfType<WriteFile>();
            foreach (var writeFile in writeFiles)
            {
                // This is telling Wolverine to handle any return value
                // of WriteFile by calling its WriteAsync() method
                writeFile.UseReturnAction(_ =>
                {
                    // This is important, return a separate MethodCall
                    // object for each individual WriteFile variable
                    return new MethodCall(typeof(WriteFile), method!)
                    {
                        Target = writeFile
                    };
                });
            }
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CustomReturnType.cs#L26-L59' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_WriteFilePolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and lastly, I'll register that policy in my Wolverine application at configuration time:

<!-- snippet: sample_register_WriteFilePolicy -->
<a id='snippet-sample_register_WriteFilePolicy'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts => { opts.Policies.Add<WriteFilePolicy>(); }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CustomReturnType.cs#L65-L70' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_register_WriteFilePolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

