# Receiving SQS Message Attributes

Here’s the deal: Amazon SQS won’t just give you the user-defined message attributes for free you have to explicitly ask for them in the receive request. Up until now, Wolverine never set that field, which meant any custom attributes upstream were effectively invisible.  

As of now, you can opt in to request those attributes. This is **interop-only**: Wolverine will ask SQS for the attributes if you configure it, but it’s still up to your own `ISqsEnvelopeMapper` to decide what to do with them.

::: tip
Built-in mappers (`DefaultSqsEnvelopeMapper`, `RawJsonSqsEnvelopeMapper`) don’t touch message attributes. If you need them, you’ll need your own mapper.
:::

## Opting in

You can request *all* user-defined attributes:

<!-- snippet: sample_receive_all_message_attributes -->
<a id='snippet-sample_receive_all_message_attributes'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport()
            .ConfigureSenders(s => s.InteropWith(new CustomSqsMapper()));

        opts.ListenToSqsQueue("incoming", queue =>
        {
            // Ask SQS for all user-defined attributes
            queue.MessageAttributeNames = new List<string> { "All" };
        });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L345-L360' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_receive_all_message_attributes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or just the attributes you actually care about:

<!-- snippet: sample_receive_specific_message_attributes -->
<a id='snippet-sample_receive_specific_message_attributes'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport()
            .ConfigureSenders(s => s.InteropWith(new CustomSqsMapper()));

        opts.ListenToSqsQueue("incoming", queue =>
        {
            // Ask only for specific attributes
            queue.MessageAttributeNames = new List<string> { "wolverineId", "jasperId" };
        });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L365-L380' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_receive_specific_message_attributes' title='Start of snippet'>anchor</a></sup>

<!-- endSnippet -->

Once you’ve opted in, those attributes are available in the dictionary passed to `ISqsEnvelopeMapper.ReadEnvelopeData`. From there, you can stash them in `Envelope.Headers`, set correlation IDs, or just ignore them.

## Things to know

* If `MessageAttributeNames` is `null` or empty, nothing changes (this is the default).  
* `"All"` asks SQS for every user-defined attribute.  
* Pulling in lots of attributes increases your payload size. Use this only when you need it.  
* This affects **receiving only**. Sending attributes is still a job for your custom mapper.  
* System attributes (`MessageSystemAttributeNames`) are a different story and are not part of this feature.  

::: info
That’s it. If you’ve already got a custom mapper, you can now wire in SQS attributes directly without having to bend over backwards with the AWS SDK.
:::