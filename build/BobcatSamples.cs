using System.Collections.Generic;
using System.Linq;
using Npgsql;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

// GH-5082: the Bobcat samples (src/Samples/Bobcat), moved here from the Bobcat repository with the Critter Stack
// vocabulary they are written against (JasperFx/bobcat#744), along with the targets that build and run them.
// `./build.sh BobcatSamples` is what bobcat-samples.yml runs.
//
// The samples are not in wolverine.slnx: each has its own .slnx, and they are written as consumers write
// applications. So Compile never touches them, and that gap once let nine of them rot undetected in Bobcat.
// Compiling was never enough to call a sample fixed, either: OutboxDemo compiled clean and could not start
// (Wolverine 6 stopped shipping the runtime compiler), and half the samples sat on an older Marten and died on a
// TypeLoadException the first time they opened a session. So these targets build every sample project AND run the
// specs of every runnable one.
//
// The sample specs are Microsoft.Testing.Platform hosts, so `dotnet test` runs them and one scenario is one test
// node. They need Postgres and nothing else.
partial class Build
{
    AbsolutePath BobcatSamplesDirectory => RootDirectory / "src" / "Samples" / "Bobcat";

    [Parameter("Postgres the Bobcat sample specs run against, without a database name - each sample gets its own")]
    readonly string BobcatSamplesPostgres = "Host=localhost;Port=5433;Username=postgres;Password=postgres";

    // Sample projects that do not compile today, named individually rather than skipped as a group. Two rules, and
    // the second is the one that matters:
    //
    //   * a project outside this list that fails  -> new rot, fail the build
    //   * a project inside this list that BUILDS  -> the list is stale, fail the build
    //
    // A quarantine that only ever forgives is how a permanently-red job becomes background noise. This one has to
    // shrink: fix a sample, and the build tells you to delete its line. Paths are relative to the repository root,
    // e.g. "src/Samples/Bobcat/OutboxDemo/Tests/Tests.csproj".
    static readonly string[] QuarantinedBobcatSamples =
    [
    ];

    // Each runnable sample and the database it uses, matching the default in its own Program.cs. Separate databases
    // rather than one shared: MeetingGroupMonolith puts its event store in a schema called `payments`, which is also
    // the schema PaymentsMonolith uses for everything, so a shared database would have each one's between-scenario
    // reset wiping the other's events.
    //
    // BankAccountES runs its Marten leg here and its Fisher leg in TestBobcatSamplesOnFisher, which is the point of
    // that sample: the same handlers against two stores.
    static readonly (string Sample, string Database)[] RunnableBobcatSamples =
    [
        ("CqrsMinimalApi", "cqrs_minimal_api"),
        ("BookingMonolith", "booking"),
        ("CleanArchitectureTodos", "clean_architecture_todos"),
        ("MoreSpeakers", "more_speakers"),
        ("PaymentsMonolith", "inflow"),
        ("EcommerceModularMonolith", "ecommerce"),
        ("MeetingGroupMonolith", "meeting_groups"),
        ("OutboxDemo", "outbox_demo"),
        ("BankAccountES", "bank_account"),
        ("EcommerceMicroservices", "ecommerce_catalog"),
        ("ProjectManagement", "project_management"),
    ];

    Target CompileBobcatSamples => _ => _
        // So one red step under LocalGate (GH-5125) does not skip the gates after it.
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var regressions = new List<string>();
            var recovered = new List<string>();

            foreach (var project in BobcatSamplesDirectory.GlobFiles("**/*.csproj").OrderBy(p => p.ToString()))
            {
                var name = RootDirectory.GetRelativePathTo(project).ToString().Replace('\\', '/');
                var quarantined = QuarantinedBobcatSamples.Contains(name);

                if (tryRunSample(() => DotNetBuild(s => s.SetProjectFile(project).SetConfiguration(Configuration))))
                {
                    if (quarantined)
                    {
                        Log.Error("{Project} builds now - remove it from QuarantinedBobcatSamples", name);
                        recovered.Add(name);
                    }
                }
                else if (quarantined)
                {
                    Log.Warning("known broken (quarantined): {Project}", name);
                }
                else
                {
                    Log.Error("{Project} failed to build", name);
                    regressions.Add(name);
                }
            }

            Log.Information("quarantined: {Count}", QuarantinedBobcatSamples.Length);
            Assert.Empty(regressions, $"Sample(s) newly broken: {string.Join(", ", regressions)}");
            Assert.Empty(recovered, $"Quarantined sample(s) now build - shrink the list: {string.Join(", ", recovered)}");
        });

    Target TestBobcatSamples => _ => _
        // So one red step under LocalGate (GH-5125) does not skip the gates after it.
        .ProceedAfterFailure()
        .DependsOn(CompileBobcatSamples)
        .Executes(() =>
        {
            // Reused when something already answers on its port (another worktree's stack, a CI service).
            StartDockerServices("postgresql");

            // Created up front because a CI service container only ever makes one database. Tolerates one that
            // already exists, so a local rerun works.
            using (var connection = new NpgsqlConnection($"{BobcatSamplesPostgres};Database=postgres"))
            {
                connection.Open();
                foreach (var (_, database) in RunnableBobcatSamples)
                {
                    using var exists = new NpgsqlCommand("select 1 from pg_database where datname = @name", connection);
                    exists.Parameters.AddWithValue("name", database);
                    if (exists.ExecuteScalar() != null) continue;

                    using var create = new NpgsqlCommand($"create database \"{database}\"", connection);
                    create.ExecuteNonQuery();
                }
            }

            var failed = new List<string>();
            foreach (var (sample, database) in RunnableBobcatSamples)
            {
                Log.Information("Running the {Sample} specs", sample);
                var passed = tryRunSample(() => DotNetTest(s => s
                    .SetProcessAdditionalArguments("--project", BobcatSamplesDirectory / sample / "Tests")
                    .SetConfiguration(Configuration)
                    .SetProcessEnvironmentVariable("ConnectionStrings__Marten", $"{BobcatSamplesPostgres};Database={database}")));

                if (!passed) failed.Add(sample);
            }

            Assert.Empty(failed, $"Sample(s) failed their specs: {string.Join(", ", failed)}");
        });

    // No services at all - a SQLite file is the whole store, which is the argument for Fisher as the inner-loop
    // store, and a sample whose point is "the same handlers run on two stores" is only proved by running both.
    Target TestBobcatSamplesOnFisher => _ => _
        // So one red step under LocalGate (GH-5125) does not skip the gates after it.
        .ProceedAfterFailure()
        .DependsOn(CompileBobcatSamples)
        .After(TestBobcatSamples)
        .Executes(() =>
        {
            var database = TemporaryDirectory / "bank_account.db";
            database.DeleteFile();

            DotNetTest(s => s
                .SetProcessAdditionalArguments("--project", BobcatSamplesDirectory / "BankAccountES" / "Tests")
                .SetConfiguration(Configuration)
                .SetProcessEnvironmentVariable("EventStore", "Fisher")
                .SetProcessEnvironmentVariable("ConnectionStrings__Fisher", $"Data Source={database}"));
        });

    // What bobcat-samples.yml runs.
    Target BobcatSamples => _ => _
        .DependsOn(CompileBobcatSamples, TestBobcatSamples, TestBobcatSamplesOnFisher);

    static bool tryRunSample(System.Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (ProcessException)
        {
            return false;
        }
    }
}
