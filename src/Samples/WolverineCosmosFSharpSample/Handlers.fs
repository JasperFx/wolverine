namespace WolverineCosmosFSharpSample

open Wolverine.Attributes
open Wolverine.CosmosDb

/// A message handler written in F# that combines two middleware surfaces:
///  * FluentValidation — the CreateThingValidator runs before this method (validation middleware).
///  * CosmosDB persistence — [<Transactional>] enlists the CosmosDB outbox, and the returned
///    ICosmosDbOp side effect (CosmosDbOps.Store) is applied within that transaction.
type CreateThingHandler =
    [<Transactional>]
    static member Handle(command: CreateThing) : ICosmosDbOp =
        CosmosDbOps.Store<Thing>({ id = command.Id; Name = command.Name })
