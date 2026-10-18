using BankAccountES.Tests;
using Bobcat;
using Microsoft.AspNetCore.Mvc.Testing;

// Names the Event Model this assembly's specs contribute slices to. Upstream merges descriptors
// BY MODEL NAME, and the Wolverine-derived model is named for the service ("BankAccount",
// Program.cs's opts.ServiceName) -- without this the generated BobcatEventModelSource would name
// its model "BankAccountES.Tests" and the spec slices could never fold into the same model as
// the chains they describe (bobcat#172).
[assembly: EventModelName("BankAccount")]

// One host for the whole run, booted once.
[assembly: AssemblyFixture(typeof(BankAccountHost))]

// Not optional. CritterStackScenario's store-only act reads back everything appended to the store
// while it ran, by sequence, and attributes it by stream (bobcat#503): another test's appends to its
// own streams are set aside as ForeignEvents, but a stream another test STARTS at the same moment
// still looks like one this act started. Whole-store assertions on a store-only act need serial
// execution on a shared store (this), or a store or tenant per test.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

// Tells ASP.NET Core's WebApplicationFactory (which Alba uses) where the host's content root
// is. Without it, discovery walks up from the test assembly's bin directory and synthesizes
// `samples/BankAccountES/BankAccountES/` from the host assembly name -- a folder that does not
// exist -- and throws DirectoryNotFoundException. The Tests project lives INSIDE the host
// project rather than beside it, which is not the layout the default assumes.
//
// The path is relative to the test assembly's bin directory at run time: from
// `samples/BankAccountES/Tests/bin/<config>/net10.0/`, four levels up is
// `samples/BankAccountES/`, where the host's appsettings.json lives.
[assembly: WebApplicationFactoryContentRoot(
    "BankAccountES",
    "../../../..",
    "appsettings.json",
    "1")]
