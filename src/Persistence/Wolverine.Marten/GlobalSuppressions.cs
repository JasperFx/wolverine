using System.Diagnostics.CodeAnalysis;

// AOT-pillar suppressions for Wolverine.Marten (#2746).
//
// Wolverine.Marten is fundamentally a runtime-codegen package over Marten:
// AggregateHandlerAttribute / WriteAggregateAttribute / ReadAggregateAttribute
// reflect over user handler methods at startup to bind aggregate / event
// stream load operations into Marten sessions. The codegen frames it emits
// then call reflective Marten APIs (IDocumentSession.Events.AggregateStreamAsync<T>,
// FetchForExclusiveWriting<T>, etc.) at the generated handler's method body.
// Saga store diagnostics also use reflective Marten document-session calls
// over runtime saga state types.
//
// All ~142 IL warnings in this package are codegen-time discovery walks over
// user aggregate / saga / event types that are statically rooted via:
//   - app-level handler discovery (chunks Q HandlerDiscovery RUC propagation)
//   - explicit MartenIntegration registration via opts.UseMarten / IntegrateWithMarten
//   - JsonSerializerOptions configured on the document store
//
// AOT-clean apps in TypeLoadMode.Static use pre-generated handler code from
// `dotnet run codegen write` and bypass the codegen frame providers entirely.
// Apps using Dynamic codegen must preserve their aggregate / saga state /
// event types via TrimmerRootDescriptor.
//
// Same chunk pattern as Wolverine.Http (chunk AC) — namespace-scoped
// suppressions to avoid spreading attributes across 19+ files; survives
// codegen frame additions and aggregate-handler refactors.

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
    Justification = "Wolverine.Marten codegen — reflection over user aggregate / saga / event types statically rooted via handler discovery + Marten registration. AOT consumers run pre-generated frames. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2055",
    Scope = "module",
    Justification = "Wolverine.Marten codegen — closed-generic helpers over user aggregate / event types at codegen time. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2057",
    Scope = "module",
    Justification = "Wolverine.Marten codegen — Type.GetType lookups for aggregate / event types statically rooted via Marten registration. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2060",
    Scope = "module",
    Justification = "Wolverine.Marten codegen — generic Marten APIs (AggregateStreamAsync<T> etc.) invoked over runtime aggregate types at codegen time. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2067",
    Scope = "module",
    Justification = "Wolverine.Marten codegen — aggregate / saga / event types statically rooted via handler discovery + Marten registration. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2070",
    Scope = "module",
    Justification = "Wolverine.Marten codegen — GetMethods / GetProperties walks over user aggregate / saga types statically rooted via handler discovery. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2072",
    Scope = "module",
    Justification = "Wolverine.Marten codegen — return-type metadata flow through codegen helpers; user types statically rooted via handler discovery. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2075",
    Scope = "module",
    Justification = "Wolverine.Marten codegen — member walks over runtime aggregate / saga types at codegen time. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2087",
    Scope = "module",
    Justification = "Wolverine.Marten codegen — generic-arg flow on aggregate handler helpers; user types statically rooted via handler discovery. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2090",
    Scope = "module",
    Justification = "Wolverine.Marten codegen — generic T accessed for member lookup; T is statically rooted via Marten registration. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2091",
    Scope = "module",
    Justification = "Wolverine.Marten codegen — generic argument T flows to a [DAM]-annotated target; T is statically rooted via Marten registration. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2111",
    Scope = "module",
    Justification = "Wolverine.Marten codegen — reflective method invoke on aggregate / saga members; bounded by Marten registration. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050",
    Scope = "module",
    Justification = "Wolverine.Marten codegen — closed generics over runtime aggregate / saga / event types at codegen time. See AOT guide.")]
