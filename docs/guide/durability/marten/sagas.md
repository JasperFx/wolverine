# Marten as Saga Storage

Marten is an easy option for [persistent sagas](/guide/durability/sagas) with Wolverine. Yet again, to opt into using Marten as your saga storage mechanism in Wolverine, you
just need to add the `IntegrateWithWolverine()` option to your Marten configuration as shown in the Marten Integration [Getting Started](/guide/durability/marten/#getting-started) section.

When using the Wolverine + Marten integration, your stateful saga classes should be valid Marten document types that inherit from Wolverine's `Saga` type, which generally means being a public class with a valid
Marten [identity member](https://martendb.io/documents/identity.html). Remember that your handler methods in Wolverine can accept "method injected" dependencies from your underlying
IoC container.

See the [Saga with Marten sample project](https://github.com/JasperFx/wolverine/tree/main/src/Samples/OrderSagaSample).

## Optimistic Concurrency <Badge type="tip" text="3.0" />

Marten will automatically apply numeric revisioning to Wolverine `Saga` storage, and will increment
the `Version` while handling `Saga` commands to use Marten's native optimistic concurrency protection.
