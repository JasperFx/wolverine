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

[assembly: UnconditionalSuppressMessage("AOT", "IL3050",
    Scope = "namespaceanddescendants",
    Target = "~N:Wolverine.Http.Newtonsoft",
    Justification = "Wolverine.Http.Newtonsoft codegen — closed generics over runtime endpoint / parameter types at codegen time. See AOT guide.")]
