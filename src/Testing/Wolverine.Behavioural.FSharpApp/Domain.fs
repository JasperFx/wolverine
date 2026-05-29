namespace WolverineBehaviouralFSharpApp

open System.Threading.Tasks

/// An in-process sink so the behavioural test can assert the generated F# handler adapter actually
/// executed at runtime (under TypeLoadMode.Static), not merely that it compiled.
module BehaviouralSink =
    let mutable private completion = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

    /// Reset before each behavioural run.
    let reset () =
        completion <- TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

    /// Called by the handler when it runs.
    let record (value: int) = completion.TrySetResult(value) |> ignore

    /// Awaited by the test.
    let received () : Task<int> = completion.Task

/// The message handled by BehaviouralPingHandler.
type BehaviouralPing = { Value: int }

/// A minimal F# handler. The generated F# adapter (Generated.fs) calls this; the behavioural test
/// boots a host in TypeLoadMode.Static, sends a BehaviouralPing, and asserts the sink recorded it.
type BehaviouralPingHandler =
    static member Handle(ping: BehaviouralPing) = BehaviouralSink.record ping.Value
