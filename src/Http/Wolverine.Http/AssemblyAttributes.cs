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
[assembly: InternalsVisibleTo("Wolverine.Http.FSharpTests")]
// GH-4605: the Polecat and Fisher HTTP deduplication mirrors assert on the generated source of an
// endpoint, which means reaching WolverineHttpOptions.Endpoints. They live in the store test projects
// rather than Wolverine.Http.Tests because those already have the containers their stores need, and
// because a store-specific endpoint type in the shared HTTP test assembly is scanned by every other
// host in it. Same grant, same reason, as Wolverine.Http.Tests above.
[assembly: InternalsVisibleTo("PolecatTests")]
[assembly: InternalsVisibleTo("FisherTests")]
// WolverineFx.Http.Newtonsoft's UseNewtonsoftJsonForSerialization extension
// needs to call HttpGraph.UseNewtonsoftJson(INewtonsoftHttpCodeGen) and
// implement INewtonsoftHttpCodeGen — both internal so the public surface
// only acknowledges the System.Text.Json default. The grant is intentional
// and scoped to this one consumer.
[assembly: InternalsVisibleTo("Wolverine.Http.Newtonsoft")]
[assembly: IgnoreAssembly]