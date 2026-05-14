# Messages and Serialization

The ultimate goal of Wolverine is to allow developers to route messages representing some work to do within the system
to the proper handler that can handle that message. Here's some facts about messages in Wolverine:

* By role, you can think of messages as either a command you want to execute or as an event raised somewhere in your system
  that you want to be handled by separate code or in a separate thread
* Messages in Wolverine **must be public types**
* Unlike other .NET messaging or command handling frameworks, there's no requirement for Wolverine messages to be an interface or require any mandatory interface or framework base classes
* Have a string identity for the message type that Wolverine will use as an identification when storing messages
  in either durable message storage or within external transports

The default serialization option is [System.Text.Json](https://learn.microsoft.com/en-us/dotnet/api/system.text.json?view=net-8.0), as this is now mature, seems to work with just about anything now, and sets you up
for relatively easy integration with a range of external non-Wolverine applications. You also have the option to fall back
to Newtonsoft.JSON or to use higher performance [MemoryPack](/guide/messages.html#memorypack-serialization) or [MessagePack](/guide/messages.html#messagepack-serialization) or [Protobuf](/guide/messages.html#protobuf-serialization) integrations with Wolverine.

## Message Type Name or Alias

Let's say that you have a basic message structure like this:

<!-- snippet: sample_personborn1 -->
<a id='snippet-sample_personborn1'></a>
```cs
public class PersonBorn
{
    public string FirstName { get; set; } = null!;
    public string LastName { get; set; } = null!;

    // This is obviously a contrived example
    // so just let this go for now;)
    public int Day { get; set; }
    public int Month { get; set; }
    public int Year { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageVersioning.cs#L13-L26' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_personborn1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

By default, Wolverine will identify this type by just using the .NET full name like so:

<!-- snippet: sample_ootb_message_alias -->
<a id='snippet-sample_ootb_message_alias'></a>
```cs
[Fact]
public void message_alias_is_fullname_by_default()
{
    new Envelope(new PersonBorn())
        .MessageType.ShouldBe(typeof(PersonBorn).FullName);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageVersioning.cs#L31-L39' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_ootb_message_alias' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

However, if you want to explicitly control the message type because you aren't sharing the DTO types or for some
other reason (readability? diagnostics?), you can override the message type alias with an attribute:

<!-- snippet: sample_override_message_alias -->
<a id='snippet-sample_override_message_alias'></a>
```cs
[MessageIdentity("person-born")]
public class PersonBorn
{
    public string FirstName { get; set; } = null!;
    public string LastName { get; set; } = null!;
    public int Day { get; set; }
    public int Month { get; set; }
    public int Year { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageVersioning.cs#L45-L56' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_override_message_alias' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Which now gives you different behavior:

<!-- snippet: sample_explicit_message_alias -->
<a id='snippet-sample_explicit_message_alias'></a>
```cs
[Fact]
public void message_alias_is_fullname_by_default()
{
    new Envelope(new PersonBorn())
        .MessageType.ShouldBe("person-born");
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageVersioning.cs#L60-L68' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_explicit_message_alias' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Message Discovery

::: tip
Wolverine does not yet support the Async API standard, but the message discovery described in this section
is also partially meant to enable that support later.
:::

Strictly for diagnostic purposes in Wolverine (like the message routing preview report in `dotnet run -- describe`), you can mark your message types to help Wolverine "discover" outgoing message 
types that will be published by the application by either implementing one of these marker interfaces (all in the main `Wolverine` namespace):

<!-- snippet: sample_message_type_discovery -->
<a id='snippet-sample_message_type_discovery'></a>
```cs
public record CreateIssue(string Name) : IMessage;

public record DeleteIssue(Guid Id) : IMessage;

public record IssueCreated(Guid Id, string Name) : IMessage;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageDiscovery.cs#L6-L13' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_message_type_discovery' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
The marker types shown above may be helpful in transitioning an existing codebase from NServiceBus to Wolverine.
:::

You can optionally use an attribute to mark a type as a message:

<!-- snippet: sample_using_wolverinemessage_attribute -->
<a id='snippet-sample_using_wolverinemessage_attribute'></a>
```cs
[WolverineMessage]
public record CloseIssue(Guid Id);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageDiscovery.cs#L15-L19' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_wolverinemessage_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or lastly, make up your own criteria to find and mark message types within your system as shown below:

<!-- snippet: sample_use_your_own_marker_type -->
<a id='snippet-sample_use_your_own_marker_type'></a>
```cs
opts.Discovery.CustomizeHandlerDiscovery(types => types.Includes.Implements<IDiagnosticsMessageHandler>());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Diagnostics/DiagnosticsApp/Program.cs#L35-L38' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_use_your_own_marker_type' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that only types that are in assemblies either marked with `[assembly: WolverineModule]` or the main application assembly
or an explicitly registered assembly will be discovered. See [Handler Discovery](/guide/handlers/discovery) for more information about the assembly scanning.


## Versioning

By default, Wolverine will just assume that any message is "V1" unless marked otherwise.
Going back to the original `PersonBorn` message class in previous sections, let's say that you
create a new version of that message that is no longer structurally equivalent to the original message:

<!-- snippet: sample_personborn_v2 -->
<a id='snippet-sample_personborn_v2'></a>
```cs
[MessageIdentity("person-born", Version = 2)]
public class PersonBornV2
{
    public string FirstName { get; set; } = null!;
    public string LastName { get; set; } = null!;
    public DateTime Birthday { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageVersioning.cs#L74-L83' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_personborn_v2' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `[MessageIdentity("person-born", Version = 2)]` attribute usage tells Wolverine that this class is "Version 2" for the `message-type` = "person-born."

Wolverine will now accept or publish this message using the built in Json serialization with the content type of `application/vnd.person-born.v2+json`.
Any custom serializers should follow some kind of naming convention for content types that identify versioned representations.


## Serialization

::: warning
Just in time for 1.0, Wolverine switched to using System.Text.Json as the default serializer instead of Newtonsoft.Json. Fingers crossed!
:::

Wolverine needs to be able to serialize and deserialize your message objects when sending messages with external transports like Rabbit MQ or when using the inbox/outbox message storage.
To that end, the default serialization is performed with [System.Text.Json](https://docs.microsoft.com/en-us/dotnet/api/system.text.json?view=net-6.0). You may also opt into using
the battle-tested Newtonsoft.Json by installing the separate `WolverineFx.Newtonsoft` NuGet package (see below).

To opt into using System.Text.Json with different defaults — which can give you better performance but with
increased risk of serialization failures — use this syntax where `opts` is a `WolverineOptions` object:

<!-- snippet: sample_opting_into_stj -->
<a id='snippet-sample_opting_into_stj'></a>
```cs
opts.UseSystemTextJsonForSerialization(stj =>
{
    stj.UnknownTypeHandling = JsonUnknownTypeHandling.JsonNode;
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Transports/Local/local_integration_specs.cs#L26-L32' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_opting_into_stj' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Newtonsoft.Json Serialization

::: tip
As of **Wolverine 6.0**, Newtonsoft.Json support lives in a separate `WolverineFx.Newtonsoft` NuGet package. The core `WolverineFx` package no longer depends on Newtonsoft.Json; install `WolverineFx.Newtonsoft` alongside it to opt in.
:::

To install:

```bash
dotnet add package WolverineFx.Newtonsoft
```

The `WolverineFx.Newtonsoft` package provides the same `UseNewtonsoftForSerialization` /
`CustomNewtonsoftJsonSerialization` API as Wolverine 5.x, now exposed as extension methods on
`WolverineOptions` and per-endpoint configuration. Add the `using Wolverine.Newtonsoft;` directive
to bring the extensions into scope.

The default configuration is:

```cs
return new JsonSerializerSettings
{
    TypeNameHandling = TypeNameHandling.Auto,
    PreserveReferencesHandling = PreserveReferencesHandling.Objects
};
```

To customize the Newtonsoft.Json serialization, use this option:

<!-- snippet: sample_customizingjsonserialization -->
<a id='snippet-sample_customizingjsonserialization'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseNewtonsoftForSerialization(settings =>
        {
            settings.ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor;
        });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageVersioning.cs#L154-L164' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_customizingjsonserialization' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### MessagePack Serialization

Wolverine supports the [MessagePack](https://github.com/neuecc/MessagePack-CSharp) serializer for message serialization through the `WolverineFx.MessagePack` Nuget package.
To enable MessagePack serialization through the entire application, use:

<!-- snippet: sample_using_messagepack_for_the_default_for_the_app -->
<a id='snippet-sample_using_messagepack_for_the_default_for_the_app'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Make MessagePack the default serializer throughout this application
        opts.UseMessagePackSerialization();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.MessagePack.Tests/Samples.cs#L10-L18' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_messagepack_for_the_default_for_the_app' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Likewise, you can use MessagePack on selected endpoints like this:

<!-- snippet: sample_using_messagepack_on_selected_endpoints -->
<a id='snippet-sample_using_messagepack_on_selected_endpoints'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Use MessagePack on a local queue
        opts.LocalQueue("one").UseMessagePackSerialization();

        // Use MessagePack on a listening endpoint
        opts.ListenAtPort(2223).UseMessagePackSerialization();

        // Use MessagePack on one subscriber
        opts.PublishAllMessages().ToPort(2222).UseMessagePackSerialization();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.MessagePack.Tests/Samples.cs#L23-L37' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_messagepack_on_selected_endpoints' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### MemoryPack Serialization

Wolverine supports the high performance [MemoryPack](https://github.com/Cysharp/MemoryPack) serializer through the `WolverineFx.MemoryPack` Nuget package.
To enable MemoryPack serialization through the entire application, use:

<!-- snippet: sample_using_memorypack_for_the_default_for_the_app -->
<a id='snippet-sample_using_memorypack_for_the_default_for_the_app'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Make MemoryPack the default serializer throughout this application
        opts.UseMemoryPackSerialization();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.MemoryPack.Tests/Samples.cs#L10-L18' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_memorypack_for_the_default_for_the_app' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Likewise, you can use MemoryPack on selected endpoints like this:

<!-- snippet: sample_using_memorypack_on_selected_endpoints -->
<a id='snippet-sample_using_memorypack_on_selected_endpoints'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Use MemoryPack on a local queue
        opts.LocalQueue("one").UseMemoryPackSerialization();

        // Use MemoryPack on a listening endpoint
        opts.ListenAtPort(2223).UseMemoryPackSerialization();

        // Use MemoryPack on one subscriber
        opts.PublishAllMessages().ToPort(2222).UseMemoryPackSerialization();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.MemoryPack.Tests/Samples.cs#L23-L37' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_memorypack_on_selected_endpoints' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Protobuf Serialization

Wolverine supports Google's data interchange format [Protobuf](https://github.com/protocolbuffers/protobuf) through the `WolverineFx.Protobuf` Nuget package.
To enable Protobuf serialization through the entire application, use:

<!-- snippet: sample_using_protobuf_for_the_default_for_the_app -->
<a id='snippet-sample_using_protobuf_for_the_default_for_the_app'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Make Protobuf the default serializer throughout this application
        opts.UseProtobufSerialization();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.Protobuf.Tests/Samples.cs#L10-L18' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_protobuf_for_the_default_for_the_app' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Likewise, you can use Protobuf on selected endpoints like this:

<!-- snippet: sample_using_protobuf_on_selected_endpoints -->
<a id='snippet-sample_using_protobuf_on_selected_endpoints'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Use Protobuf on a local queue
        opts.LocalQueue("one").UseProtobufSerialization();

        // Use Protobuf on a listening endpoint
        opts.ListenAtPort(2223).UseProtobufSerialization();

        // Use Protobuf on one subscriber
        opts.PublishAllMessages().ToPort(2222).UseProtobufSerialization();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Extensions/Wolverine.Protobuf.Tests/Samples.cs#L23-L37' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_protobuf_on_selected_endpoints' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->




## Versioned Message Forwarding

If you make breaking changes to an incoming message in a later version, you can simply handle both versions of that message separately:

<!-- snippet: sample_personcreatedhandler -->
<a id='snippet-sample_personcreatedhandler'></a>
```cs
public class PersonCreatedHandler
{
    public static void Handle(PersonBorn person)
    {
        // do something w/ the message
    }

    public static void Handle(PersonBornV2 person)
    {
        // do something w/ the message
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageVersioning.cs#L107-L121' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_personcreatedhandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or you could use a custom `IMessageDeserializer` to read incoming messages from V1 into the new V2 message type, or you can take advantage of message forwarding
so you only need to handle one message type using the `IForwardsTo<T>` interface as shown below:

<!-- snippet: sample_iforwardsto_personbornv2 -->
<a id='snippet-sample_iforwardsto_personbornv2'></a>
```cs
public class PersonBorn : IForwardsTo<PersonBornV2>
{
    public string FirstName { get; set; } = null!;
    public string LastName { get; set; } = null!;
    public int Day { get; set; }
    public int Month { get; set; }
    public int Year { get; set; }

    public PersonBornV2 Transform()
    {
        return new PersonBornV2
        {
            FirstName = FirstName,
            LastName = LastName,
            Birthday = new DateTime(Year, Month, Day)
        };
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageVersioning.cs#L85-L105' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_iforwardsto_personbornv2' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Which forwards to the current message type:

<!-- snippet: sample_personborn_v2 -->
<a id='snippet-sample_personborn_v2'></a>
```cs
[MessageIdentity("person-born", Version = 2)]
public class PersonBornV2
{
    public string FirstName { get; set; } = null!;
    public string LastName { get; set; } = null!;
    public DateTime Birthday { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageVersioning.cs#L74-L83' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_personborn_v2' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Using this strategy, other systems could still send your system the original `application/vnd.person-born.v1+json` formatted
message, and on the receiving end, Wolverine would know to deserialize the Json data into the `PersonBorn` object, then call its
`Transform()` method to build out the `PersonBornV2` type that matches up with your message handler.

## "Self Serializing" Messages

::: info
This was originally built for an unusual MQTT requirement, but is going to be used extensively by Wolverine
internals as a tiny optimization
:::

This is admittedly an oddball use case for micro-optimization, but you may embed the serialization logic for a message type right into the 
message type itself through Wolverine's `ISerializable` interface as shown below:

<!-- snippet: sample_intrinsic_serialization -->
<a id='snippet-sample_intrinsic_serialization'></a>
```cs
public class SerializedMessage : ISerializable
{
    public string Name { get; set; } = "Bob Schneider";

    public byte[] Write()
    {
        return Encoding.UTF8.GetBytes(Name);
    }

    // You'll need at least C# 11 for static methods
    // on interfaces!
    public static object Read(byte[] bytes)
    {
        var name = Encoding.UTF8.GetString(bytes);
        return new SerializedMessage { Name = name };
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Serialization/intrinsic_serialization.cs#L21-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_intrinsic_serialization' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine will see the interface implementation of the message type, and automatically opt into using this "intrinsic"
serialization.

## Envelope ID Generation <Badge type="tip" text="5.26" />

Every message envelope in Wolverine is assigned a unique `Guid` identifier. By default, Wolverine uses [MassTransit's NewId](https://masstransit.io/documentation/patterns/newid) algorithm which generates sequential GUIDs based on machine identity (MAC address or DNS hostname) and a timestamp. This works well for most scenarios and provides good database index performance, especially on SQL Server.

However, in **cloud environments where multiple instances share network identity** — such as Kubernetes pods, Google Cloud Run, AWS Lambda, or other containerized/serverless platforms — `NewId` can produce **duplicate envelope IDs** because it relies on machine-specific properties that are identical across instances.

For **greenfield applications**, or any deployment where instances may share network identity, use `GuidV7`:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Recommended for new applications and cloud deployments
        opts.EnvelopeIdGeneration = EnvelopeIdGeneration.GuidV7;
    }).StartAsync();
```

`GuidV7` uses `Guid.CreateVersion7()` per [RFC 9562](https://www.rfc-editor.org/rfc/rfc9562), which produces time-ordered GUIDs with cryptographically strong random bits. This guarantees uniqueness without relying on machine-specific properties, while still maintaining good database index performance due to time-ordering.

::: warning
`EnvelopeIdGeneration.GuidV7` requires **.NET 9 or later**. `Guid.CreateVersion7()` is not available on .NET 8. Attempting to use this option on .NET 8 will throw a `NotSupportedException` at startup.
:::

| Strategy | Uniqueness Source | Time-Ordered | Best For |
|----------|------------------|-------------|----------|
| `NewId` (default) | Machine identity + timestamp | Yes | Single-machine or unique-network deployments |
| `GuidV7` | Cryptographic randomness + timestamp | Yes | Cloud, containers, serverless, greenfield apps |

