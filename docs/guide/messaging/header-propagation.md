# Header Propagation

Wolverine can automatically forward headers from an incoming message to all outgoing messages produced within the same handler context. This is useful for propagating correlation identifiers, tracing metadata, or contextual information like "on-behalf-of" across a chain of messages.

## Propagating a Single Header

Use `PropagateIncomingHeaderToOutgoing` when you need to forward just one header:

```csharp
builder.Host.UseWolverine(opts =>
{
    // Forward the "on-behalf-of" header to all downstream messages
    opts.Policies.PropagateIncomingHeaderToOutgoing("x-on-behalf-of");
});
```

This is ideal for scenarios like middleware that detects delegated user actions and marks outgoing messages to support downstream logging and auditing:

```csharp
// In your middleware, set the header on the incoming envelope:
context.Envelope.Headers["x-on-behalf-of"] = impersonatedUser;

// Any messages published within this handler context will
// automatically carry the "x-on-behalf-of" header
```

## Propagating Multiple Headers

Use `PropagateIncomingHeadersToOutgoing` to forward several headers at once:

```csharp
builder.Host.UseWolverine(opts =>
{
    opts.Policies.PropagateIncomingHeadersToOutgoing("x-correlation-id", "x-source-system");
});
```

## Behavior

When a handler receives a message carrying any of the named headers, Wolverine will copy those headers onto every outgoing message cascaded within that handler context. Headers not present on the incoming message are silently skipped — no errors are thrown.

This works across all transports — Kafka, RabbitMQ, Azure Service Bus, or any other.

::: warning
The headers must be present on the incoming `Envelope` at the point the handler runs. Wolverine's default envelope mappers only carry Wolverine's own metadata headers, so if you need to propagate custom headers from an external producer you will need a custom envelope mapper that explicitly reads those headers from the transport message and sets them on the envelope.
:::

## Custom Envelope Rules

For more advanced header manipulation, you can implement a custom `IEnvelopeRule`:

```csharp
public class OnBehalfOfRule : IEnvelopeRule
{
    // Called when publishing outside a handler context
    public void Modify(Envelope envelope) { }

    // Called within a handler context with access to the incoming message
    public void ApplyCorrelation(IMessageContext originator, Envelope outgoing)
    {
        var incoming = originator.Envelope;
        if (incoming is null) return;

        if (incoming.Headers.TryGetValue("x-on-behalf-of", out var value))
        {
            outgoing.Headers["x-on-behalf-of"] = value;
            outgoing.Headers["x-audit-trail"] = $"delegated:{value}";
        }
    }
}
```

Register your custom rule as a metadata rule:

```csharp
builder.Host.UseWolverine(opts =>
{
    opts.MetadataRules.Add(new OnBehalfOfRule());
});
```
