using System.Diagnostics.CodeAnalysis;

// Code.cs in this namespace is auto-generated QuickType output for parsing
// Azure Service Bus emulator configuration JSON. The generator emits standard
// reflection-based JsonSerializer.Serialize/Deserialize calls (IL2026/IL3050)
// — there's no way to teach QuickType to emit JsonSerializerContext-backed
// code, and patching Code.cs would be reverted on the next regen.
//
// The emulator config types are unbounded by design (the QuickType output
// is a dump of every Service Bus emulator JSON shape) and only used in
// test/dev scenarios — never on the per-message dispatch path. Suppress at
// the namespace level so the suppression survives Code.cs regeneration.
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
    Justification = "Auto-generated QuickType emulator config; test/dev only, not on dispatch path. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050",
    Scope = "module",
    Justification = "Auto-generated QuickType emulator config; test/dev only, not on dispatch path. See AOT guide.")]
