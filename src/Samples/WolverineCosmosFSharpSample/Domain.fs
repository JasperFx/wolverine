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
// begin-snippet: sample_fsharp_fluentvalidation_validator
type CreateThingValidator() as self =
    inherit AbstractValidator<CreateThing>()

    do
        self.RuleFor(fun x -> x.Id).NotEmpty() |> ignore
        self.RuleFor(fun x -> x.Name).NotEmpty() |> ignore
// end-snippet

// ── Cosmos saga ──────────────────────────────────────────────────────────────
// These types exercise LoadDocumentFrame.GenerateFSharpCode in the compile gate.

/// Start command for the saga; the Id becomes the Cosmos document id.
type StartThingSaga = { Id: string }

/// Continue command; SagaId links back to the saga document.
type ContinueThing = { SagaId: string }

open Wolverine

/// A minimal Cosmos-persisted saga.  Start creates the document; Handle loads it
/// via LoadDocumentFrame (the CosmosDB frame under test).
/// AllowNullLiteral is required so the Wolverine-generated `isNull sagaVar` guard compiles
/// for F# class types (C# classes are implicitly nullable; F# classes are not by default).
[<AllowNullLiteral>]
type ThingSaga() =
    inherit Saga()
    member val Id = "" with get, set
    member val Count = 0 with get, set

    static member Start(command: StartThingSaga) : ThingSaga =
        ThingSaga(Id = command.Id, Count = 1)

    member this.Handle(_: ContinueThing) : unit =
        this.Count <- this.Count + 1

// ── Cosmos saga started or continued by one message ─────────────────────────
// StartOrHandle loads the saga like any other handler, and when there is none,
// CreateMissingSagaFrame creates it before the same method runs. Deliberately NOT
// [<AllowNullLiteral>]: the generated null guard boxes the loaded saga, so a plain
// F# class works.

/// Starts a TallySaga, or adds to the one already stored. TallySagaId names the saga.
type AddToTally = { TallySagaId: string; Amount: int }

/// Cascaded each time the tally changes.
type TallyChanged = { TallyId: string; Total: int }

type TallySaga() =
    inherit Saga()
    member val Id = "" with get, set
    member val Total = 0 with get, set

    member this.StartOrHandle(command: AddToTally) : TallyChanged =
        if System.String.IsNullOrEmpty this.Id then
            this.Id <- command.TallySagaId

        this.Total <- this.Total + command.Amount
        { TallyId = this.Id; Total = this.Total }
