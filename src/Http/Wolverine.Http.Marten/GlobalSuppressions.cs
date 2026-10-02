using System.Diagnostics.CodeAnalysis;

// AOT-pillar suppressions for Wolverine.Http.Marten (#2746).
//
// Wolverine.Http.Marten extends Wolverine.Http codegen with Marten-aware
// frame providers — CompiledQueryWriterPolicy reflects over runtime
// IQueryHandler<T> implementations at codegen time to wire Marten compiled
// queries into HTTP endpoint result-writing. Same shape as the parent
// Wolverine.Marten + Wolverine.Http packages, both already AOT-flagged.
//
// All IL warnings in this package are codegen-time reflection over user
// compiled-query / aggregate / event types that are statically rooted via:
//   - app-level handler discovery (chunks Q HandlerDiscovery RUC propagation)
//   - explicit Marten document-store registration
//   - HTTP endpoint discovery in MapWolverineEndpoints
//
// AOT-clean apps in TypeLoadMode.Static use pre-generated HTTP handler code
// from `dotnet run codegen write` and bypass these frame providers entirely.

//
// GH-4788: Scope = "module", not "namespaceanddescendants". ILC REJECTS the namespace scopes --
// `namespaceanddescendants`, `namespace` and `resource` all produce IL2108 ("Invalid scope ... used in
// UnconditionalSuppressMessageAttribute"), and a rejected suppression suppresses NOTHING, so under a
// Native AOT publish every warning below was being reported to the consumer. Roslyn accepts those scopes,
// which is why this went unnoticed; no AOT lane referenced any of these packages until GH-4778 added one.
// Only "module", "type" and "member" are honoured by ILC -- measured, not inferred -- and "module" is the
// one that preserves the intent stated above of covering the whole package without spreading attributes
// across 20+ files. It is marginally wider than the namespace scope it replaces, since it also covers any
// type in this assembly outside that namespace.

[assembly: UnconditionalSuppressMessage("Trimming", "IL2026",
    Scope = "module",
    Justification = "Wolverine.Http.Marten codegen — closed-generic compiled-query helpers over user query / response types statically rooted via Marten registration. AOT consumers run pre-generated frames. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2072",
    Scope = "module",
    Justification = "Wolverine.Http.Marten codegen — Variable.VariableType flow into Closes/FindInterfaceThatCloses; query/handler types statically rooted via Marten registration. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050",
    Scope = "module",
    Justification = "Wolverine.Http.Marten codegen — closed generics over runtime compiled-query types at codegen time. See AOT guide.")]
