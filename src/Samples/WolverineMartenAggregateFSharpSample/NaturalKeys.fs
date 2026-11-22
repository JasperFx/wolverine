namespace WolverineMartenAggregateFSharpSample

open System
open JasperFx.Events
open JasperFx.Events.Aggregation
open Marten.Events.Aggregation
open Wolverine.Marten

/// A natural key: a struct wrapping a string, so a command can also carry it as Nullable<LedgerCode>.
[<Struct>]
type LedgerCode = { Value: string }

type LedgerOpened = { Id: Guid; Code: LedgerCode }
type Deposited = { Amount: int }

/// An aggregate found by its natural key as well as by its stream id.
[<CLIMutable>]
type Ledger =
    { Id: Guid
      [<NaturalKey>]
      Code: LedgerCode
      Balance: int }

/// Evolve folds the events, and NaturalKeyFor says which event sets the key: the C#-only source generator
/// that reads [NaturalKeySource] methods does not run for F#.
type LedgerProjection() as this =
    inherit SingleStreamProjection<Ledger, Guid>()

    do this.NaturalKeyFor(fun x -> x.SetBy<LedgerOpened>(fun e -> box e.Code) |> ignore)

    override _.Evolve(snapshot: Ledger, _id: Guid, e: IEvent) : Ledger =
        match e.Data with
        | :? LedgerOpened as opened -> { Id = opened.Id; Code = opened.Code; Balance = 0 }
        | :? Deposited as deposited -> { snapshot with Balance = snapshot.Balance + deposited.Amount }
        | _ -> snapshot

/// Loaded by key at the expected version
type DepositByCode = { Code: LedgerCode; Version: int64; Amount: int }

/// Loaded by key with the stream locked
type DepositExclusively = { Code: LedgerCode; Amount: int }

/// A key that can be null is refused on a write
type DepositByOptionalCode = { Code: Nullable<LedgerCode>; Amount: int }

type AuditLedger = { Code: LedgerCode }

/// A key that can be null reads nothing
type AuditLedgerByOptionalCode = { Code: Nullable<LedgerCode> }

/// Every way the aggregate handler workflow loads by natural key. F# cannot apply a generic attribute, so
/// each names the key type with NaturalKey = typeof<LedgerCode>. Each load is Required = false because a
/// required [WriteAggregate] or [ReadAggregate] parameter adds a missing-aggregate guard that has no F# form.
type LedgerHandler =
    static member Handle(command: DepositByCode,
                         [<WriteAggregate(NaturalKey = typeof<LedgerCode>, Required = false)>] ledger: Ledger) : Deposited =
        { Amount = command.Amount }

    static member Handle(command: DepositExclusively,
                         [<WriteAggregate(NaturalKey = typeof<LedgerCode>, LoadStyle = ConcurrencyStyle.Exclusive, Required = false)>] ledger: Ledger) : Deposited =
        { Amount = command.Amount }

    static member Handle(command: DepositByOptionalCode,
                         [<WriteAggregate(NaturalKey = typeof<LedgerCode>, Required = false)>] ledger: Ledger) : Deposited =
        { Amount = command.Amount }

    static member Handle(_command: AuditLedger,
                         [<ReadAggregate(NaturalKey = typeof<LedgerCode>, Required = false)>] ledger: Ledger) : unit =
        ignore ledger

    static member Handle(_command: AuditLedgerByOptionalCode,
                         [<ReadAggregate(NaturalKey = typeof<LedgerCode>, Required = false)>] ledger: Ledger) : unit =
        ignore ledger
