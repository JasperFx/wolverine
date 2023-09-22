# Best Practices

There will be more content here soon:)

* Minimize dependencies, put functionality directly into handler classes
* Utilize "compound handlers" to keep logic or transformations in pure functions for easier testability
* Lean on transactional middleware and/or cascading message returns when possible for easier testability
* Prefer method injection over constructor injection
* Try to only publish cascading events in the root message handler to make the message flow easier to understand
* "A-Frame" architecture
* One handler per message type unless trivial


## Attaining IMessageBus

The Wolverine team recommendation is to utilize [cascading messages](/guide/handlers/cascading) as much as possible to publish additional messages,
but when you do need to attain a reference to an `IMessageBus` object, we suggest to take that service into your
message handler or HTTP endpoint methods as a method argument like so:

```csharp
public static async Task HandleAsync(MyMessage message, IMessageBus messageBus)
{
    // handle the message..;
}
```

or if you really prefer the little extra ceremony of constructor injection because that's how the .NET ecosystem has
worked for the past 15-20 years, do this:

```csharp

public class MyMessageHandler
{
    private readonly IMessageBus _messageBus;

    public MyMessageHandler(IMessageBus messageBus)
    {
        _messageBus = messageBus;
    }
    
    public async Task HandleAsync(MyMessage message, IMessageBus messageBus)
    {
         // handle the message..;
    }
}
```

Avoid ever trying to resolve `IMessageBus` at runtime through a scoped container like this:

```csharp
services.AddScoped<IService>(s => {
    var bus = s.GetRequiredService<IMessageBus>();
    return new Service { TenantId = bus.TenantId };
});
```

The usage above will give you a completely different `IMessageBus` object than the current `MessageContext` being used
by Wolverine to track behavior and state.

## Avoid Runtime IoC in Wolverine

::: tip
Wolverine is trying really hard **not** to use an IoC container whatsoever at runtime. It's not going to work with 
Wolverine to try to pass state between the outside world into Wolverine through ASP.Net Core scoped containers for
example.
:::

Honestly, you'll get better performance and better results all the way around if you can avoid doing any kind of "opaque"
service registrations in your IoC container that require runtime resolution. In effect, this means to stay away from 
any kind of `Scoped` or `Transient` Lambda registration like:

```csharp
services.AddScoped<IDatabase>(s => {
    var c = s.GetRequiredService<IConfiguration>();
    return new Database(c.GetConnectionString("foo");
});
```
