using OutboxDemo.Tests;
using Microsoft.AspNetCore.Mvc.Testing;

// One host for the whole run, booted once.
[assembly: AssemblyFixture(typeof(OutboxDemoHost))]

// Every spec empties the store before it runs (see OutboxDemoSpec), so two specs running at once would
// wipe each other's data.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

// Tells ASP.NET Core's WebApplicationFactory (which Alba uses) where the host's content root
// is. Without it, discovery walks up from the test assembly's bin directory and synthesizes
// `samples/OutboxDemo/OutboxDemo/` from the host assembly name — a folder that does not exist —
// and throws DirectoryNotFoundException. The Tests project lives INSIDE the host project rather
// than beside it, which is not the layout the default assumes.
//
// The path is relative to the test assembly's bin directory at run time: from
// `samples/OutboxDemo/Tests/bin/<config>/net10.0/`, four levels up is `samples/OutboxDemo/`,
// where the host's appsettings.json lives.
[assembly: WebApplicationFactoryContentRoot(
    "OutboxDemo",
    "../../../..",
    "appsettings.json",
    "1")]
