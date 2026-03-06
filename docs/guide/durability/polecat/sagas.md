# Polecat as Saga Storage

Polecat is an easy option for [persistent sagas](/guide/durability/sagas) with Wolverine. To opt into using Polecat as your saga storage mechanism in Wolverine, you
just need to add the `IntegrateWithWolverine()` option to your Polecat configuration as shown in the Polecat Integration [Getting Started](/guide/durability/polecat/#getting-started) section.

When using the Wolverine + Polecat integration, your stateful saga classes should be valid Polecat document types that inherit from Wolverine's `Saga` type, which generally means being a public class with a valid
identity member. Remember that your handler methods in Wolverine can accept "method injected" dependencies from your underlying
IoC container.

## Optimistic Concurrency

Polecat will automatically apply numeric revisioning to Wolverine `Saga` storage, and will increment
the `Version` while handling `Saga` commands to use Polecat's native optimistic concurrency protection.
