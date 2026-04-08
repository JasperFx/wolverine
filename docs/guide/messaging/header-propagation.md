# Header Propagation

When consuming messages from external systems, those messages may carry custom headers that need to flow through to any downstream messages your handlers produce. Use `PropagateIncomingHeadersToOutgoing` to declare which headers should be forwarded automatically:

```csharp
builder.Host.UseWolverine(opts =>
{
    opts.Policies.PropagateIncomingHeadersToOutgoing("x-correlation-id", "x-source-system");
});
```

When a handler receives a message carrying any of the named headers, Wolverine will copy those headers onto every outgoing message cascaded within that handler context. Headers not present on the incoming message are silently skipped.

This works across all transports — Kafka, RabbitMQ, Azure Service Bus, or any other. The headers must be present on the incoming `Envelope` at the point the handler runs. Wolverine's default envelope mappers only carry Wolverine's own metadata headers, so if you need to propagate custom headers from an external producer you will need a custom envelope mapper that explicitly reads those headers from the transport message and sets them on the envelope.
