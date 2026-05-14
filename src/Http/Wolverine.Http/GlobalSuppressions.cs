using System.Diagnostics.CodeAnalysis;

// AOT-pillar suppressions for Wolverine.Http (#2746).
//
// Wolverine.Http is fundamentally a runtime-codegen package: HttpChain walks
// user endpoint methods reflectively at startup to bind route values, query
// strings, form fields, headers, JSON bodies, and AsParameters-decorated
// records. The codegen frames it emits then call the same reflective APIs
// (PropertyInfo.GetValue, MethodInfo.Invoke, Type.GetMethod, etc.) at the
// generated handler's method body — but those calls happen against statically
// rooted user types because the source-generated handler class names them
// directly.
//
// All ~128 IL warnings in this package are codegen-time discovery walks over
// user endpoint / parameter types that are statically rooted via:
//   - app.MapWolverineEndpoints() / [WolverineHandler] discovery
//   - explicit endpoint registration through MapXxx<T>(...)
//   - JsonBodyParameterStrategy reads against the user's JsonSerializerOptions
//
// AOT-clean apps in TypeLoadMode.Static use pre-generated endpoint handlers
// from `dotnet run codegen write`; this Dynamic-mode codegen path doesn't
// fire at runtime. Apps using Dynamic codegen must preserve their endpoint
// types and JSON DTOs via TrimmerRootDescriptor.
//
// Same chunk pattern as Wolverine.csproj chunks A–U + Wolverine.EntityFrameworkCore
// chunk AB. Namespace-scoped suppressions to avoid spreading attributes across
// 20+ files; survives partial-class additions and codegen frame additions.

[assembly: UnconditionalSuppressMessage("Trimming", "IL2026",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Http",
    Justification = "Wolverine.Http codegen — reflection-based STJ over user endpoint / parameter / JSON-body types statically rooted via endpoint discovery. AOT consumers run pre-generated frames in TypeLoadMode.Static. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2055",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Http",
    Justification = "Wolverine.Http codegen — closed-generic helper types over user endpoint / parameter types at codegen time. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2057",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Http",
    Justification = "Wolverine.Http codegen — Type.GetType lookups for endpoint / parameter / wire types statically rooted via endpoint discovery. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2060",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Http",
    Justification = "Wolverine.Http codegen — generic method invocation over runtime endpoint / parameter types at codegen time. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2067",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Http",
    Justification = "Wolverine.Http codegen — endpoint / parameter / JSON-body types statically rooted via endpoint discovery. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2070",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Http",
    Justification = "Wolverine.Http codegen — GetMethods / GetProperties walks over user endpoint types statically rooted via endpoint discovery. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2072",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Http",
    Justification = "Wolverine.Http codegen — return-type metadata flow through codegen helpers; user types statically rooted via endpoint discovery. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2075",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Http",
    Justification = "Wolverine.Http codegen — member walks over runtime endpoint / parameter types at codegen time. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2087",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Http",
    Justification = "Wolverine.Http codegen — generic-arg flow on parameter binding helpers; user types statically rooted via endpoint discovery. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2090",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Http",
    Justification = "Wolverine.Http codegen — generic-parameter T accessed for member lookup; T is statically rooted via endpoint registration. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2091",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Http",
    Justification = "Wolverine.Http codegen — generic argument T flows to a [DAM]-annotated target; T is statically rooted via endpoint registration. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2111",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Http",
    Justification = "Wolverine.Http codegen — reflective method invoke on endpoint members; bounded by endpoint discovery. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Http",
    Justification = "Wolverine.Http codegen — closed generics over runtime endpoint / parameter types at codegen time. See AOT guide.")]
