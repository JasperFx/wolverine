using ProjectManagement.Tests;
using Microsoft.AspNetCore.Mvc.Testing;

// One host for the whole run, booted once.
[assembly: AssemblyFixture(typeof(ProjectManagementHost))]

// Every spec empties the store before it runs (see ProjectManagementSpec), so two specs running at once would
// wipe each other's data.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

// Tell ASP.NET Core's WebApplicationFactory (used by Alba) where to find the host project's content
// root. The path is relative to the test assembly's bin directory at run time: from
// `Tests/bin/<config>/net10.0/`, four levels up is the sample's folder.
[assembly: WebApplicationFactoryContentRoot(
    "ProjectManagement.Api",
    "../../../../ProjectManagement.Api",
    "appsettings.json",
    "1")]
