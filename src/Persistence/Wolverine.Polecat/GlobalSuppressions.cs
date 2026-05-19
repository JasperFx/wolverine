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

[assembly: UnconditionalSuppressMessage("Trimming", "IL2026",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Polecat",
    Justification = "Wolverine.Polecat codegen — reflection over user aggregate / event types statically rooted via handler discovery + Polecat registration. AOT consumers run pre-generated frames. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2060",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Polecat",
    Justification = "Wolverine.Polecat codegen — generic Polecat APIs (event-stream load / aggregate fetch) invoked over runtime aggregate types at codegen time. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2065",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Polecat",
    Justification = "Wolverine.Polecat codegen — 'this' arg flow on reflective member lookups over runtime aggregate / event types. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2067",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Polecat",
    Justification = "Wolverine.Polecat codegen — aggregate / event types statically rooted via handler discovery + Polecat registration. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2070",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Polecat",
    Justification = "Wolverine.Polecat codegen — GetMethods / GetProperties walks over user aggregate types statically rooted via handler discovery. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2072",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Polecat",
    Justification = "Wolverine.Polecat codegen — return-type metadata flow through codegen helpers; user types statically rooted via handler discovery. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2075",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Polecat",
    Justification = "Wolverine.Polecat codegen — member walks over runtime aggregate types at codegen time. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2091",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Polecat",
    Justification = "Wolverine.Polecat codegen — generic argument T flows to a [DAM]-annotated target; T is statically rooted via Polecat registration. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Polecat",
    Justification = "Wolverine.Polecat codegen — closed generics over runtime aggregate / event types at codegen time. See AOT guide.")]
