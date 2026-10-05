using System.Diagnostics.CodeAnalysis;

// AOT-pillar suppressions for Wolverine.Http.Newtonsoft (#2742 / #2746).
//
// Mirrors the namespace-scoped pattern in Wolverine.Http's GlobalSuppressions.cs.
// This package re-introduces the Newtonsoft-flavored HTTP codegen frames that
// previously lived in core Wolverine.Http; the IL warnings come from the same
// codegen-time MakeGenericMethod / generic-arg flow over user endpoint /
// parameter / JSON-body types that Wolverine.Http already suppresses for the
// System.Text.Json branch. Same justification: user types are statically rooted
// via endpoint discovery, AOT consumers run pre-generated frames in
// TypeLoadMode.Static.

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

[assembly: UnconditionalSuppressMessage("AOT", "IL3050",
    Scope = "module",
    Justification = "Wolverine.Http.Newtonsoft codegen — closed generics over runtime endpoint / parameter types at codegen time. See AOT guide.")]
