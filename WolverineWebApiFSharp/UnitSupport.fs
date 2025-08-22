namespace WolverineWebApiFSharp.UnitSupport

open System.Threading.Tasks
open Wolverine.Http

type Command = { SomeProperty: string }

// In F# the type Unit defines the absence of a value, similar to void in C#.
// In C# it is represented as a Task with no result, which is equivalent to Task<Unit>.
// But the codegen treats the Unit type as a regular type and generates a cascaded message call for it.
// I've only encountered this in the context of Task handlers, but will add more examples if I find them.

module TaskUnitSupport =
    (* Codegen looks like this:
        public override async System.Threading.Tasks.Task Handle(Microsoft.AspNetCore.Http.HttpContext httpContext)
        {
            var messageContext = new Wolverine.Runtime.MessageContext(_wolverineRuntime);
            // Reading the request body via JSON deserialization
            var (command, jsonContinue) = await ReadJsonAsync<WolverineWebApiFSharp.UnitSupport.Command>(httpContext);
            if (jsonContinue == Wolverine.HandlerContinuation.Stop) return;
            
            // The actual HTTP request handler execution
            var unit = await WolverineWebApiFSharp.UnitSupport.TaskUnitSupport.post(command).ConfigureAwait(false);

            
            // Outgoing, cascaded message
            await messageContext.EnqueueCascadingAsync(unit).ConfigureAwait(false);

            // Wolverine automatically sets the status code to 204 for empty responses
            if (httpContext.Response is { HasStarted: false, StatusCode: 200 }) httpContext.Response.StatusCode = 204;
            
            // Have to flush outgoing messages just in case Marten did nothing because of https://github.com/JasperFx/wolverine/issues/536
            await messageContext.FlushOutgoingMessagesAsync().ConfigureAwait(false);

        }
    *)            
            
    [<WolverinePost("/unit-support/task")>]
    [<EmptyResponse>]
    let post (command: Command) = task {
        do! Task.Delay 10
    }
     