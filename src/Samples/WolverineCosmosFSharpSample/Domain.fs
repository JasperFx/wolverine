namespace WolverineCosmosFSharpSample

open FluentValidation

/// The CosmosDB document persisted by the sample. Cosmos identifies documents by a lowercase `id`.
[<CLIMutable>]
type Thing = { id: string; Name: string }

/// The command handled by CreateThingHandler.
type CreateThing = { Id: string; Name: string }

/// The event cascaded out after the document is stored.
type ThingCreated = { Id: string }

/// FluentValidation validator for CreateThing. Wolverine's FluentValidation middleware runs this
/// before the handler (and short-circuits with a failure result if invalid). F# auto-converts the
/// property-selector lambdas to the LINQ expression trees RuleFor expects.
type CreateThingValidator() as self =
    inherit AbstractValidator<CreateThing>()

    do
        self.RuleFor(fun x -> x.Id).NotEmpty() |> ignore
        self.RuleFor(fun x -> x.Name).NotEmpty() |> ignore
