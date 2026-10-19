using CqrsMinimalApi.Tests;
using Microsoft.AspNetCore.Mvc.Testing;

// One host for the whole run, booted once.
[assembly: AssemblyFixture(typeof(CqrsMinimalApiHost))]

// Every spec empties the store before it runs (see CqrsMinimalApiSpec), so two specs running at
// once would wipe each other's students.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

// Tell ASP.NET Core's WebApplicationFactory (used by Alba) where to find the
// host project's content root. Without this, Alba walks up from the test
// assembly's bin directory and tries `samples/CqrsMinimalApi/CqrsMinimalApi/`
// — a non-existent extra folder synthesized from the host assembly name —
// and bombs with `DirectoryNotFoundException`. The Tests project lives
// INSIDE the host project (`samples/CqrsMinimalApi/Tests/`) rather than as
// a sibling, which is the layout the default discovery assumes.
//
// The contentRootPath is relative to the test assembly's bin directory at
// runtime. From `samples/CqrsMinimalApi/Tests/bin/Debug/net10.0/`, four
// levels up gets us to `samples/CqrsMinimalApi/` where the host's
// `appsettings.json` lives — which is what Alba/WAF needs to anchor on.
[assembly: WebApplicationFactoryContentRoot(
    "CqrsMinimalApi",
    "../../../..",
    "appsettings.json",
    "1")]
