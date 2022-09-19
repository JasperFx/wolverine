# Messages and Serialization

The ultimate goal of Wolverine is to allow developers to route messages representing some work to do within the system
to the proper handler that can handle that message. Here's some facts about messages in Wolverine:

* By role, you can think of messages as either a command you want to execute or as an event raised somewhere in your system
  that you want to be handled by separate code or in a separate thread
* Messages in Wolverine **must be public types**
* Unlike other .NET messaging or command handling frameworks, there's no requirement for Wolverine messages to be an interface or require any mandatory interface or framework base classes
* Have a string identity for the message type that Wolverine will use as an identification when storing messages
  in either durable message storage or within external transports

## Message Type Name or Alias

Let's say that you have a basic message structure like this:

<!-- snippet: sample_PersonBorn1 -->
<a id='snippet-sample_personborn1'></a>
```cs
public class PersonBorn
{
    public string FirstName { get; set; }
    public string LastName { get; set; }

    // This is obviously a contrived example
    // so just let this go for now;)
    public int Day { get; set; }
    public int Month { get; set; }
    public int Year { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/MessageVersioning.cs#L15-L29' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_personborn1' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/MessageVersioning.cs#L34-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_ootb_message_alias' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

However, if you want to explicitly control the message type because you aren't sharing the DTO types or for some
other reason (readability? diagnostics?), you can override the message type alias with an attribute:

<!-- snippet: sample_override_message_alias -->
<a id='snippet-sample_override_message_alias'></a>
```cs
[MessageIdentity("person-born")]
public class PersonBorn
{
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public int Day { get; set; }
    public int Month { get; set; }
    public int Year { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/MessageVersioning.cs#L49-L61' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_override_message_alias' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/MessageVersioning.cs#L65-L74' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_explicit_message_alias' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Versioning

By default, Wolverine will just assume that any message is "V1" unless marked otherwise.
Going back to the original `PersonBorn` message class in previous sections, let's say that you
create a new version of that message that is no longer structurally equivalent to the original message:

<!-- snippet: sample_PersonBorn_V2 -->
<a id='snippet-sample_personborn_v2'></a>
```cs
[MessageIdentity("person-born", Version = 2)]
public class PersonBornV2
{
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public DateTime Birthday { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/MessageVersioning.cs#L80-L90' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_personborn_v2' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `[Version("V2")]` attribute usage tells Wolverine that this class is "V2" for the `message-type` = "person-born."

Wolverine will now accept or publish this message using the built in Json serialization with the content type of `application/vnd.person-born.v2+json`.
Any custom serializers should follow some kind of naming convention for content types that identify versioned representations.


## Serialization

Wolverine needs to be able to serialize and deserialize your message objects when sending messages with external transports like Rabbit MQ or when using the inbox/outbox message storage.
To that end, the default serialization is performed with Newtonsoft.Json because of its ubiquity and "battle testedness," but
you may also opt into using [System.Text.Json](https://docs.microsoft.com/en-us/dotnet/api/system.text.json?view=net-6.0).

When using Newtonsoft.Json, the default configuration is:

<!-- snippet: sample_default_newtonsoft_settings -->
<a id='snippet-sample_default_newtonsoft_settings'></a>
```cs
return new JsonSerializerSettings
{
    TypeNameHandling = TypeNameHandling.Auto,
    PreserveReferencesHandling = PreserveReferencesHandling.Objects
};
```
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Wolverine/Runtime/Serialization/NewtonsoftSerializer.cs#L20-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_default_newtonsoft_settings' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To customize the Newtonsoft.Json serialization, use this option:

<!-- snippet: sample_CustomizingJsonSerialization -->
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
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/MessageVersioning.cs#L164-L175' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_customizingjsonserialization' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And to instead opt into using System.Text.Json -- which can give you better performance but with
increased risk of serialization failures -- use this syntax where `opts` is a `WolverineOptions` object:

<!-- snippet: sample_opting_into_STJ -->
<a id='snippet-sample_opting_into_stj'></a>
```cs
opts.UseSystemTextJsonForSerialization(stj =>
{
    stj.UnknownTypeHandling = JsonUnknownTypeHandling.JsonNode;
});
```
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Testing/CoreTests/Transports/Local/local_integration_specs.cs#L23-L30' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_opting_into_stj' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Versioned Message Forwarding

If you make breaking changes to an incoming message in a later version, you can simply handle both versions of that message separately:

<!-- snippet: sample_PersonCreatedHandler -->
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
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/MessageVersioning.cs#L115-L130' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_personcreatedhandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or you could use a custom `IMessageDeserializer` to read incoming messages from V1 into the new V2 message type, or you can take advantage of message forwarding
so you only need to handle one message type using the `IForwardsTo<T>` interface as shown below:

<!-- snippet: sample_IForwardsTo_PersonBornV2 -->
<a id='snippet-sample_iforwardsto_personbornv2'></a>
```cs
public class PersonBorn : IForwardsTo<PersonBornV2>
{
    public string FirstName { get; set; }
    public string LastName { get; set; }
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
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/MessageVersioning.cs#L92-L113' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_iforwardsto_personbornv2' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Which forwards to the current message type:

<!-- snippet: sample_PersonBorn_V2 -->
<a id='snippet-sample_personborn_v2'></a>
```cs
[MessageIdentity("person-born", Version = 2)]
public class PersonBornV2
{
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public DateTime Birthday { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/alba/blob/master/src/Samples/DocumentationSamples/MessageVersioning.cs#L80-L90' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_personborn_v2' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Using this strategy, other systems could still send your system the original `application/vnd.person-born.v1+json` formatted
message, and on the receiving end, Wolverine would know to deserialize the Json data into the `PersonBorn` object, then call its
`Transform()` method to build out the `PersonBornV2` type that matches up with your message handler.





