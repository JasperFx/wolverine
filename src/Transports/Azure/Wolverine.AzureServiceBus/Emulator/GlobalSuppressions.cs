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
[assembly: UnconditionalSuppressMessage("Trimming", "IL2026",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.AzureServiceBus.Emulator",
    Justification = "Auto-generated QuickType emulator config; test/dev only, not on dispatch path. See AOT guide.")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.AzureServiceBus.Emulator",
    Justification = "Auto-generated QuickType emulator config; test/dev only, not on dispatch path. See AOT guide.")]
