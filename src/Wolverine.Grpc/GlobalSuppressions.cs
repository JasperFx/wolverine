using System.Diagnostics.CodeAnalysis;

// AOT-pillar suppressions for Wolverine.Grpc (#2746).
//
// Wolverine.Grpc is a runtime-codegen + service-discovery package:
//   - CodeFirstGrpcServiceChain / HandWrittenGrpcServiceChain / GrpcServiceChain
//     close gRPC service-contract types over user message types at codegen
//     time, emitting stub classes via JasperFx.CodeGeneration's
//     GeneratedAssembly.AddType(name, baseType).
//   - WolverineGrpcExtensions uses MakeGenericMethod to wire transport
//     registration over discovered service-contract types.
//   - GrpcGraph builds OptionsDescription instances for diagnostic surfaces
//     (reflects over property metadata of user contract types).
//
// All IL warnings in this package are codegen-time / service-discovery
// reflection over user gRPC service-contract types that are statically rooted
// via:
//   - explicit MapWolverineGrpc / AddGrpcEndpoints registration
//   - the protobuf-net.Grpc + Grpc.AspNetCore code-first / contract-first
//     service definitions (themselves AOT-friendly when consumers follow
//     the Grpc.Tools source-gen path)
//
// AOT-clean apps in TypeLoadMode.Static use pre-generated service stubs from
// `dotnet run codegen write` and bypass these chain providers entirely.
// Apps using Dynamic codegen must preserve their gRPC service-contract types
// + their generated message classes via TrimmerRootDescriptor.
//
// Same namespace-scoped pattern as Wolverine.Marten / Wolverine.Polecat —
// keeps the suppressions in one file rather than sprinkling attributes
// across the codegen chain providers.

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
    Justification = "Wolverine.Grpc codegen — JasperFx OptionsDescription diagnostic surface reflects over service-contract properties; service types statically rooted via gRPC registration. AOT consumers run pre-generated frames. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2060",
    Scope = "module",
    Justification = "Wolverine.Grpc codegen — MakeGenericMethod closes transport-registration helpers over user service-contract types statically rooted via registration. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2070",
    Scope = "module",
    Justification = "Wolverine.Grpc codegen — GetMethods walks over user service-contract types statically rooted via gRPC registration. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2072",
    Scope = "module",
    Justification = "Wolverine.Grpc codegen — service-contract Type flow into GeneratedAssembly.AddType(name, baseType); base types statically rooted via gRPC registration. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2075",
    Scope = "module",
    Justification = "Wolverine.Grpc codegen — member walks over runtime service-contract types at codegen time. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2091",
    Scope = "module",
    Justification = "Wolverine.Grpc codegen — generic argument T flows to a [DAM]-annotated target; T is statically rooted via gRPC service registration. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050",
    Scope = "module",
    Justification = "Wolverine.Grpc codegen — MakeGenericMethod over runtime service-contract types at codegen time. See AOT guide.")]
