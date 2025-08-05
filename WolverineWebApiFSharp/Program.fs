
open System
open JasperFx
open Marten
open Microsoft.AspNetCore.Builder
open Wolverine
open Wolverine.Http

// We'll come back to this later. Used it for now to manually test some changes
// let args = Environment.GetCommandLineArgs()[1..]
//
// let builder = WebApplication.CreateBuilder(args)
//
// builder.Host.UseWolverine() |> ignore
// builder.Services.AddWolverineHttp() |> ignore
// builder.Services.AddMarten("Host=localhost;Port=12345;Username=postgres;Password=postgres;Database=postgres") |> ignore
//
// let app = builder.Build()
// app.MapWolverineEndpoints();
// app.RunJasperFxCommands(args).GetAwaiter().GetResult() |> ignore