# Polecat Integration

::: info
There is also some HTTP specific integration for Polecat with Wolverine. See [Integration with Polecat](/guide/http/polecat) for more information.
:::

[Polecat](https://github.com/JasperFx/polecat) and Wolverine are sibling projects under the [JasperFx organization](https://github.com/JasperFx), and as such, have quite a bit of synergy when used together. Adding the `WolverineFx.Polecat` Nuget dependency to your application adds the capability to combine Polecat and Wolverine to:

* Simplify persistent handler coding with transactional middleware
* Use Polecat and SQL Server as a persistent inbox or outbox with Wolverine messaging
* Support persistent sagas within Wolverine applications
* Effectively use Wolverine and Polecat together for a [Decider](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider) function workflow with event sourcing
* Selectively publish events captured by Polecat through Wolverine messaging
* Process events captured by Polecat through Wolverine message handlers through either [subscriptions](./subscriptions) or the older [event forwarding](./event-forwarding).

## Getting Started

To use the Wolverine integration with Polecat, install the `WolverineFx.Polecat` Nuget into your application. Assuming that you've configured Polecat in your application (and Wolverine itself!), you next need to add the Wolverine integration to Polecat as shown in this sample application bootstrapping:

```cs
var builder = WebApplication.CreateBuilder(args);
builder.Host.ApplyJasperFxExtensions();

builder.Services.AddPolecat(opts =>
    {
        opts.Connection(connectionString);
    })
    .IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    opts.Policies.AutoApplyTransactions();
});
```

Using the `IntegrateWithWolverine()` extension method behind your call to `AddPolecat()` will:

* Register the necessary [inbox and outbox](/guide/durability/) database tables with Polecat's database schema management
* Adds Wolverine's "DurabilityAgent" to your .NET application for the inbox and outbox
* Makes Polecat the active [saga storage](/guide/durability/sagas) for Wolverine
* Adds transactional middleware using Polecat to your Wolverine application

## Transactional Middleware

See the [Transactional Middleware](./transactional-middleware) page.

## Polecat as Outbox

See the [Polecat as Outbox](./outbox) page.

## Polecat as Inbox

See the [Polecat as Inbox](./inbox) page.

## Saga Storage

See the [Polecat as Saga Storage](./sagas) page.
