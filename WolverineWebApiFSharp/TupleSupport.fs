namespace WolverineWebApiFSharp.TupleSupport

open System
open Microsoft.AspNetCore.Mvc
open Wolverine.Http

type Command = { SomeProperty: string }

// The root problem is, that F# uses System.Tuple by default, but C# uses System.ValueTuple when defining a tuple with (int, string) syntax.
// A workaround for F# is to use struct(int, string) which compiles to ValueTuple.
// That affects at least the ability to pass values from Before/Validate.. methods to the handler method,
// as well as the ability to use cascading messages in the handler method.

module ValidationSupport =
    (* Codegen looks like this:
        public override async System.Threading.Tasks.Task Handle(Microsoft.AspNetCore.Http.HttpContext httpContext)
        {
            // Reading the request body via JSON deserialization
            var (command, jsonContinue) = await ReadJsonAsync<WolverineWebApiFSharp.TupleSupport.Command>(httpContext);
            if (jsonContinue == Wolverine.HandlerContinuation.Stop) return;
            var validationResult = httpContext.Request.Query["validationResult"];
            var tuple = WolverineWebApiFSharp.TupleSupport.ValidationSupport.Validate(command);
            
            // The actual HTTP request handler execution
            var result_of_post = WolverineWebApiFSharp.TupleSupport.ValidationSupport.post(command, validationResult);

            await WriteString(httpContext, result_of_post);
        }
    *)
    
    let Validate (command: Command) =
        if String.IsNullOrWhiteSpace(command.SomeProperty) then
            ProblemDetails(Status = 400), ""
        else
            WolverineContinue.NoProblems, command.SomeProperty
            
            
    [<WolverinePost("/tuple-support/validate")>]
    let post (command: Command, validationResult: string) =
        validationResult
        

module CascadingMessages =
    (* Codegen looks like this
        public override async System.Threading.Tasks.Task Handle(Microsoft.AspNetCore.Http.HttpContext httpContext)
        {
            // Reading the request body via JSON deserialization
            var (command, jsonContinue) = await ReadJsonAsync<WolverineWebApiFSharp.TupleSupport.Command>(httpContext);
            if (jsonContinue == Wolverine.HandlerContinuation.Stop) return;
            
            // The actual HTTP request handler execution
            var tuple_response = WolverineWebApiFSharp.TupleSupport.AdditionalReturnValues.post(command);

            // Writing the response body to JSON because this was the first 'return variable' in the method signature
            await WriteJsonAsync(httpContext, tuple_response);
        }
    *)
    
    type Event = { Id: Guid }
    
    [<WolverinePost("/tuple-support/cascading-messages")>]
    let post (command: Command) =
        let event = { Id = Guid.NewGuid() }
        event.Id, event