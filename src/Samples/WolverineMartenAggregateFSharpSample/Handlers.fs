namespace WolverineMartenAggregateFSharpSample

open Wolverine.Marten

/// A Marten event-sourced aggregate handler written in F#. [<AggregateHandler>] tells Wolverine to
/// load the Counter aggregate for the stream named by the command's CounterId (FetchForWriting), pass
/// it to this method, and append the returned event(s) back to that stream. The returned Incremented
/// is registered against the loaded stream by the generated adapter.
type IncrementHandler =
    [<AggregateHandler>]
    static member Handle(command: IncrementCounter, counter: Counter) : Incremented =
        { By = command.By }
