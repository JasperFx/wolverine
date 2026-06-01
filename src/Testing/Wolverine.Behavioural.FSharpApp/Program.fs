module WolverineBehaviouralFSharpApp.Program

open Microsoft.Extensions.Hosting
open JasperFx
open Wolverine
open WolverineBehaviouralFSharpApp

// Entry point so the app answers the JasperFx CLI verbs — notably
// `dotnet run -- codegen write --language fsharp`, which regenerates the F# handler adapter.
// The Wolverine configuration here MUST match BehaviouralCodegen.Configure in the test project
// (DisableConventionalDiscovery + IncludeType<BehaviouralPingHandler>) so the generated handler
// type-name hash is identical to the one the behavioural run-step computes under TypeLoadMode.Static.
[<EntryPoint>]
let main args =
    Host
        .CreateDefaultBuilder(args)
        .UseWolverine(fun opts ->
            opts.Discovery.DisableConventionalDiscovery() |> ignore
            opts.Discovery.IncludeType<BehaviouralPingHandler>() |> ignore)
        .RunJasperFxCommands(args)
        .GetAwaiter()
        .GetResult()
