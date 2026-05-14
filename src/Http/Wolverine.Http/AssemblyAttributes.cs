using System.Runtime.CompilerServices;
using JasperFx.Core.TypeScanning;

[assembly: InternalsVisibleTo("Wolverine.Http.Tests")]
// WolverineFx.Http.Newtonsoft's UseNewtonsoftJsonForSerialization extension
// needs to call HttpGraph.UseNewtonsoftJson(INewtonsoftHttpCodeGen) and
// implement INewtonsoftHttpCodeGen — both internal so the public surface
// only acknowledges the System.Text.Json default. The grant is intentional
// and scoped to this one consumer.
[assembly: InternalsVisibleTo("Wolverine.Http.Newtonsoft")]
[assembly: IgnoreAssembly]