using System.Diagnostics.CodeAnalysis;

// AOT-pillar suppressions for Wolverine.Polecat (#2746).
//
// Wolverine.Polecat is the SQL Server counterpart to Wolverine.Marten and has
// the same shape: WriteAggregateAttribute, the AggregateHandling codegen
// frames, EventWrapperForwarder, and the Subscriptions / PublishingRelay /
// InlineInvoker entry points reflect over user aggregate / event types at
// codegen time to bind Polecat session operations into the generated handler
// pipeline. UpdatedAggregate inspects user strong-typed-id types via
// ValueTypeInfo.ForType to discover the id shape.
//
// All IL warnings in this package are codegen-time discovery walks over user
// aggregate / saga / event types that are statically rooted via:
//   - app-level handler discovery (chunks Q HandlerDiscovery RUC propagation)
//   - explicit Polecat registration via opts.UsePolecat / IntegrateWithPolecat
//
// AOT-clean apps in TypeLoadMode.Static use pre-generated handler code from
// `dotnet run codegen write` and bypass the codegen frame providers entirely.
// Apps using Dynamic codegen must preserve their aggregate / saga state /
// event types via TrimmerRootDescriptor.
//
// Same namespace-scoped pattern as Wolverine.Marten — keeps the suppressions
// in one file and survives codegen frame additions / aggregate-handler
// refactors without sprinkling attributes across the package.

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
    Justification = "Wolverine.Polecat codegen — reflection over user aggregate / event types statically rooted via handler discovery + Polecat registration. AOT consumers run pre-generated frames. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2060",
    Scope = "module",
    Justification = "Wolverine.Polecat codegen — generic Polecat APIs (event-stream load / aggregate fetch) invoked over runtime aggregate types at codegen time. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2065",
    Scope = "module",
    Justification = "Wolverine.Polecat codegen — 'this' arg flow on reflective member lookups over runtime aggregate / event types. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2067",
    Scope = "module",
    Justification = "Wolverine.Polecat codegen — aggregate / event types statically rooted via handler discovery + Polecat registration. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2070",
    Scope = "module",
    Justification = "Wolverine.Polecat codegen — GetMethods / GetProperties walks over user aggregate types statically rooted via handler discovery. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2072",
    Scope = "module",
    Justification = "Wolverine.Polecat codegen — return-type metadata flow through codegen helpers; user types statically rooted via handler discovery. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2075",
    Scope = "module",
    Justification = "Wolverine.Polecat codegen — member walks over runtime aggregate types at codegen time. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2091",
    Scope = "module",
    Justification = "Wolverine.Polecat codegen — generic argument T flows to a [DAM]-annotated target; T is statically rooted via Polecat registration. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050",
    Scope = "module",
    Justification = "Wolverine.Polecat codegen — closed generics over runtime aggregate / event types at codegen time. See AOT guide.")]
