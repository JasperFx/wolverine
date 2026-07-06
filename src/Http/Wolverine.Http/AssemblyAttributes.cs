using System.Runtime.CompilerServices;
using JasperFx;
using JasperFx.Core.TypeScanning;

// Marks WolverineFx.Http as a JasperFx command-line extension assembly so the
// 'openapi' command (and any future Wolverine.Http commands) are discovered by
// RunJasperFxCommands() in consuming applications. See OpenApiCommand.
[assembly: JasperFxAssembly]

[assembly: InternalsVisibleTo("Wolverine.Http.AspVersioning")]
[assembly: InternalsVisibleTo("Wolverine.Http.AspVersioning.Tests")]

[assembly: InternalsVisibleTo("Wolverine.Http.Tests")]
// WolverineFx.Http.Newtonsoft's UseNewtonsoftJsonForSerialization extension
// needs to call HttpGraph.UseNewtonsoftJson(INewtonsoftHttpCodeGen) and
// implement INewtonsoftHttpCodeGen — both internal so the public surface
// only acknowledges the System.Text.Json default. The grant is intentional
// and scoped to this one consumer.
[assembly: InternalsVisibleTo("Wolverine.Http.Newtonsoft")]
[assembly: IgnoreAssembly]