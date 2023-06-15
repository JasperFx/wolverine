# Isolating Side Effects from Handlers

::: tip
For easier unit testing, it's often valuable to separate responsibilities of "deciding" what to do from the actual "doing." The side
effect facility in Wolverine is an example of this strategy.
:::

At times, you may with to make Wolverine message handlers (or HTTP endpoints) be [pure functions](https://en.wikipedia.org/wiki/Pure_function)
as a way of making the handler code itself easier to test or even just to understand. All the same, your application
will almost certainly be interacting with the outside world of databases, file systems, and external infrastructure of all types.
Not to worry though, Wolverine has some facility to allow you to declare the *[side effects](https://en.wikipedia.org/wiki/Side_effect_(computer_science))*
as return values from your handler. 

To make this concrete, let's say that we're building a message handler that will take in some textual content and an id, and then
try to write that text to a file at a certain path. In our case, we want to be able to easily unit test the logic that "decides" what
content and what file path a message should be written to without ever having any usage of the actual file system (which is notoriously
irritating to use in tests).

First off, I'm going to create a new "side effect" type for writing a file like this:

<!-- snippet: sample_WriteFile -->
<a id='snippet-sample_writefile'></a>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CustomReturnType.cs#L12-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_writefile' title='Start of snippet'>anchor</a></sup>
<a id='snippet-sample_writefile-1'></a>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/using_custom_side_effect.cs#L41-L67' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_writefile-1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And the matching message type, message handler, and a settings class for configuration:

<!-- snippet: sample_RecordTextHandler -->
<a id='snippet-sample_recordtexthandler'></a>
```cs
// An options class
public class PathSettings
{
    public string Directory { get; set; } 
        = Environment.CurrentDirectory.AppendPath("files");
}

public record RecordText(Guid Id, string Text);

public class RecordTextHandler
{
    public WriteFile Handle(RecordText command)
    {
        return new WriteFile(command.Id + ".txt", command.Text);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/using_custom_side_effect.cs#L20-L39' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_recordtexthandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

At runtime, Wolverine is generating this code to handle the `RecordText` message:

```csharp
    public class RecordTextHandler597515455 : Wolverine.Runtime.Handlers.MessageHandler
    {
        public override System.Threading.Tasks.Task HandleAsync(Wolverine.Runtime.MessageContext context, System.Threading.CancellationToken cancellation)
        {
            var recordTextHandler = new CoreTests.Acceptance.RecordTextHandler();
            var recordText = (CoreTests.Acceptance.RecordText)context.Envelope.Message;
            var pathSettings = new CoreTests.Acceptance.PathSettings();
            var outgoing1 = recordTextHandler.Handle(recordText);
            
            // Placed by Wolverine's ISideEffect policy
            return outgoing1.ExecuteAsync(pathSettings);
        }
    }
```

To explain what is happening up above, when Wolverine sees that any return value from a message
handler implements the `Wolverine.ISideEffect` interface, Wolverine knows that that value
should have a method named either `Execute` or `ExecuteAsync()` that should be executed
instead of treating the return value as a cascaded message. The method discovery is completely
by method name, and it's perfectly legal to use arguments for any of the same types
available to the actual message handler like:

* Service dependencies from the application's IoC container
* The actual message
* Any objects created by middleware
* `CancellationToken`
* Message metadata from `Envelope`

You can find more usages of side effect return values in the [Marten side effect operations](/guide/durability/marten/operations).

