using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.Npm;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main () => Execute<Build>(x => x.Test);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
    
    [Solution(GenerateProjects = true)]
    readonly Solution Solution;
    
    [Parameter]readonly string Framework;
    [Parameter] readonly string PostgresConnectionString ="Host=localhost;Port=5433;Database=postgres;Username=postgres;password=postgres";

    [Parameter] readonly string SqlServerConnectionString =
        "Server=localhost,1434;User Id=sa;Password=P@55w0rd;Timeout=5;Initial Catalog=master;Encrypt=False";

    Target Init => _ => _
        .Executes(Clean);

    Target Restore => _ => _
        .DependsOn(Init)
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .SetFramework(Framework)
                .EnableNoRestore());
        });

    Target CI => _ => _
        .DependsOn(CoreTests, CIMessageRouting);

    Target Test => _ => _
        .DependsOn(CoreTests, TestExtensions, Commands, PolicyTests, HttpTests);

    Target Full => _ => _
        .DependsOn(Test, PersistenceTests, SqliteTests, RabbitmqTests, PulsarTests);

    // Every test target in this file goes through RunTestProject rather than calling DotNetTest
    // directly, so they all get the flaky-retry harness and the standard Category!=Flaky filter.
    // CoreTests is the one that matters most: the `CI` target above is what the .NET workflow runs,
    // so before GH-3705 the largest core suite was the only thing in CI without a second attempt.
    Target CoreTests => _ => _
        .DependsOn(Compile)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            RunTestProject(Solution.Testing.CoreTests);
        });
   
    Target PolicyTests => _ => _
        .DependsOn(Compile, DockerUp)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            RunTestProject(Solution.Testing.PolicyTests);
        });

    Target TestExtensions => _ => _
        .DependsOn(FluentValidationTests, DataAnnotationsValidationTests, MemoryPackTests, MessagePackTests);
    
    Target FluentValidationTests => _ => _
        .DependsOn(Compile)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            RunTestProject(Solution.Extensions.Wolverine_FluentValidation_Tests);
        });

    Target DataAnnotationsValidationTests => _ => _
        .DependsOn(Compile)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            RunTestProject(Solution.Extensions.Wolverine_DataAnnotationsValidation_Tests);
        });

    Target MemoryPackTests => _ => _
        .DependsOn(Compile)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            RunTestProject(Solution.Extensions.Wolverine_MemoryPack_Tests);
        });
    
    Target MessagePackTests => _ => _
        .DependsOn(Compile)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            RunTestProject(Solution.Extensions.Wolverine_MessagePack_Tests);
        });

    Target HttpTests => _ => _
        .DependsOn(CoreHttpTests);

    Target CoreHttpTests => _ => _
        .DependsOn(Compile, DockerUp)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            RunTestProject(Solution.Http.Wolverine_Http_Tests);
        });

    Target Commands => _ => _
        .DependsOn(HelpCommand, DescribeCommand, CodegenPreviewCommand);
    
    Target DescribeCommand => _ => _
        .DependsOn(Compile)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetRun(c => c
                .SetProjectFile(Solution.Testing.ConsoleApp)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework)
                .AddApplicationArguments("describe"));
        });
    
    Target HelpCommand => _ => _
        .DependsOn(Compile)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetRun(c => c
                .SetProjectFile(Solution.Testing.ConsoleApp)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework)
                .AddApplicationArguments("?"));
        });
    
    Target CodegenPreviewCommand => _ => _
        .DependsOn(Compile)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetRun(c => c
                .SetProjectFile(Solution.Http.WolverineWebApi)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework)
                .AddApplicationArguments("codegen")
                .AddApplicationArguments("preview"));
        });

    // Exercises the Wolverine.Http `openapi` command (GH-2903) against the TodoWebService sample,
    // which uses Marten/PostgreSQL-backed message persistence and AddOpenApi(). Modeled on
    // CodegenPreviewCommand above. This intentionally runs without a database (no docker services
    // started) to prove the command generates the OpenAPI document straight from endpoint metadata
    // without any database connectivity. Builds on demand so it can run as a standalone http CI step.
    Target OpenApiCommand => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var project = RootDirectory / "src" / "Samples" / "TodoWebService" / "TodoWebService" /
                          "TodoWebService.csproj";

            // Full document for the default "v1" document. --no-launch-profile keeps the host
            // environment (e.g. ASPNETCORE_ENVIRONMENT=Development on CI) intact instead of letting
            // launchSettings.json override it; Development turns on DI scope validation, which also
            // guards the GH-2911 fix.
            DotNetRun(c => c
                .SetProjectFile(project)
                .SetConfiguration(Configuration)
                .EnableNoLaunchProfile()
                .SetFramework(Framework)
                .AddApplicationArguments("openapi"));

            // The fuzzy --route filter, which emits only the matching paths and their schemas
            DotNetRun(c => c
                .SetProjectFile(project)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoLaunchProfile()
                .SetFramework(Framework)
                .AddApplicationArguments("openapi")
                .AddApplicationArguments("--route")
                .AddApplicationArguments("todoitems"));
        });

    Target SqliteTests => _ => _
        .DependsOn(Compile)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            RunTestProject(Solution.Persistence.Sqlite.SqliteTests);
        });

    Target PersistenceTests => _ => _
        .DependsOn(Compile, DockerUp)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            RunTestProject(Solution.Persistence.PersistenceTests);
        });
    
    Target RabbitmqTests => _ => _
        .DependsOn(Compile, DockerUp)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            RunTestProject(Solution.Transports.RabbitMQ.Wolverine_RabbitMQ_Tests);
        });
    
    Target PulsarTests => _ => _
        .DependsOn(Compile, DockerUp)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            RunTestProject(Solution.Transports.Pulsar.Wolverine_Pulsar_Tests);
        });

    Target TestSamples => _ => _
        .DependsOn(TodoWebServiceSampleTests, BankingServiceSampleTests, 
            AppWithMiddlewareSampleTests, ItemServiceSampleTests);
   
    Target TodoWebServiceSampleTests => _ => _
        .DependsOn(Compile, DockerUp)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            RunTestProject(Solution.Samples.TodoWebService.TodoWebServiceTests);
        });
   
    Target BankingServiceSampleTests => _ => _
        .DependsOn(Compile, DockerUp)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            RunTestProject(Solution.Samples.TestHarness.BankingService_Tests);
        });

    Target AppWithMiddlewareSampleTests => _ => _
        .DependsOn(Compile, DockerUp)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            RunTestProject(Solution.Samples.Middleware.AppWithMiddleware_Tests);
        });

    Target ItemServiceSampleTests => _ => _
        .DependsOn(Compile, DockerUp)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            RunTestProject(Solution.Samples.EFCoreSample.ItemService_Tests);
        });

    // Every project that Pack publishes to nuget.org. Kept as a property rather than a local so
    // ValidatePackList can check it against the projects that actually declare a <PackageId> —
    // see GH-3905, where WolverineFx.DataAnnotationsValidation and WolverineFx.FluentValidation.Grpc
    // declared a PackageId, were documented as installable packages, and had never been published
    // because nothing tied the two lists together.
    Project[] NugetProjects =>
        new[]
            {
                Solution.Wolverine,
                Solution.Wolverine_RuntimeCompilation,
                Solution.Wolverine_HealthChecks,
                Solution.Transports.RabbitMQ.Wolverine_RabbitMQ,
                Solution.Transports.Azure.Wolverine_AzureServiceBus,
                Solution.Transports.AWS.Wolverine_AmazonSqs,
                Solution.Transports.AWS.Wolverine_AmazonSns,
                Solution.Transports.MQTT.Wolverine_MQTT,
                Solution.Transports.MQTT.Wolverine_Mqtt5,
                Solution.Transports.Kafka.Wolverine_Kafka,
                Solution.Transports.Pulsar.Wolverine_Pulsar,
                Solution.Transports.GCP.Wolverine_Pubsub,
                Solution.Persistence.Wolverine_RDBMS,
                Solution.Persistence.PostgreSQL.Wolverine_Postgresql,
                Solution.Persistence.Marten.Wolverine_Marten,
                Solution.Persistence.RavenDb.Wolverine_RavenDb,
                Solution.Persistence.SqlServer.Wolverine_SqlServer,
                Solution.Persistence.MySql.Wolverine_MySql,
                Solution.Persistence.Oracle.Wolverine_Oracle,
                Solution.Persistence.Sqlite.Wolverine_Sqlite,
                Solution.Persistence.CosmosDb.Wolverine_CosmosDb,
                Solution.Persistence.ClaimCheck.Wolverine_ClaimCheck_AmazonS3,
                Solution.Persistence.ClaimCheck.Wolverine_ClaimCheck_AzureBlobStorage,
                Solution.Persistence.ClaimCheck.Wolverine_ClaimCheck_GoogleCloudStorage,
                Solution.Persistence.ClaimCheck.Wolverine_ClaimCheck_Marten,
                Solution.Persistence.ClaimCheck.Wolverine_ClaimCheck_Nats,
                Solution.Persistence.ClaimCheck.Wolverine_ClaimCheck_Postgresql,
                Solution.Persistence.ClaimCheck.Wolverine_ClaimCheck_SqlServer,
                Solution.Extensions.Wolverine_FluentValidation,
                Solution.Extensions.Wolverine_FluentValidation_Grpc,
                Solution.Extensions.Wolverine_DataAnnotationsValidation,
                Solution.Extensions.Wolverine_MemoryPack,
                Solution.Extensions.Wolverine_MessagePack,
                Solution.Extensions.Wolverine_Newtonsoft,
                Solution.Extensions.Wolverine_Protobuf,
                Solution.Http.Wolverine_Http,
                Solution.Http.Wolverine_Http_AspVersioning,
                Solution.Http.Wolverine_Http_Newtonsoft,
                Solution.Http.Wolverine_Http_FluentValidation,
                Solution.Http.Wolverine_Http_Marten,
                Solution.Persistence.Polecat.Wolverine_Http_Polecat,
                Solution.Persistence.Fisher.Wolverine_Http_Fisher,
                Solution.Testing.Wolverine_ComplianceTests,
                Solution.Transports.Redis.Wolverine_Redis,
                Solution.Transports.SignalR.Wolverine_SignalR,
                Solution.Transports.NATS.Wolverine_Nats,
                Solution.Grpc.Wolverine_Grpc,
                Solution.Persistence.EFCore.Wolverine_EntityFrameworkCore,
                Solution.Persistence.Polecat.Wolverine_Polecat,
                Solution.Persistence.Fisher.Wolverine_Fisher
            };

    // GH-3905: a project can declare a <PackageId> and still never ship, because the Pack list above
    // is maintained by hand. That is exactly how WolverineFx.DataAnnotationsValidation and
    // WolverineFx.FluentValidation.Grpc stayed unpublished at every version while the docs told users
    // to install them. This fails the build the moment the two lists drift again.
    Target ValidatePackList => _ => _
        .Executes(() =>
        {
            var packed = NugetProjects
                .Select(x => x.Path.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var declaresPackageId = RootDirectory
                .GlobFiles("src/**/*.csproj")
                .Where(path => Regex.IsMatch(File.ReadAllText(path), @"<PackageId>", RegexOptions.IgnoreCase))
                .Select(path => path.ToString())
                .ToArray();

            var missing = declaresPackageId
                .Where(path => !packed.Contains(path))
                .OrderBy(x => x)
                .ToArray();

            if (missing.Length > 0)
            {
                throw new Exception(
                    $"{missing.Length} project(s) declare a <PackageId> but are missing from the Pack target's project list, so they would never be published:{Environment.NewLine}" +
                    string.Join(Environment.NewLine, missing.Select(x => "  - " + Path.GetRelativePath(RootDirectory, x))) +
                    $"{Environment.NewLine}Either add them to Build.NugetProjects, or remove the <PackageId> if they are not meant to ship.");
            }

            Log.Information("All {Count} projects declaring a <PackageId> are in the Pack list", declaresPackageId.Length);
        });

    Target Pack => _ => _
        .DependsOn(ValidatePackList)
        .Executes(() =>
        {
            foreach (var project in NugetProjects)
            {
                DotNetPack(s => s
                    .SetProject(project)
                    .SetOutputDirectory("./artifacts")
                    .SetConfiguration(Configuration.Release));
            }
        });

    Target DockerUp => _ => _
        .Executes(() =>
        {
            // Shares ComposeUp with the CI targets so this path gets the same registry-timeout
            // retry — it pulls every image in the compose file, so it is the most exposed of all.
            ComposeUp("compose up -d", "all services");
            WaitForDatabaseToBeReady();
        });

    # region Docs
    Target NpmInstall => _ => _
        .Executes(() => NpmTasks.NpmInstall());
    
    Target InstallMdSnippets => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            const string toolName = "markdownSnippets.tool";
            
            if (IsDotNetToolInstalled(toolName))
            {
                Log.Information($"{toolName} is already installed, skipping this step.");
                return;
            }
            
            DotNetToolInstall(c => c
                .SetPackageName(toolName)
                .EnableGlobal());
        });
    
    Target Docs => _ => _
        .DependsOn(NpmInstall, InstallMdSnippets)
        .Executes(() => NpmTasks.NpmRun(s => s.SetCommand("docs")));

    Target DocsBuild => _ => _
        .DependsOn(NpmInstall, InstallMdSnippets)
        .Executes(() => NpmTasks.NpmRun(s => s.SetCommand("docs:build")));


    Target PublishDocs => _ => _
        .DependsOn(DocsBuild)
        .Executes(() => NpmTasks.NpmRun(s => s.SetCommand("docs:publish")));
    
    #endregion

    
    static void Clean()
    {
        var results = AbsolutePath.Create("results");
        var artifacts = AbsolutePath.Create("artifacts");
        results.CreateOrCleanDirectory();
        artifacts.CreateOrCleanDirectory();
    }
    
    bool IsDotNetToolInstalled(string toolName)
    {
        var process = ProcessTasks.StartProcess("dotnet", "tool list -g", logOutput: false);
        process.AssertZeroExitCode();
        var output = process.Output.Select(x => x.Text).ToList();

        return output.Any(line => line.Contains(toolName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Postgres readiness. This was the thinnest gate in the build by a wide margin: ten attempts
    /// separated by 250ms is a <b>2.5 second</b> budget for a container start, after which it logged
    /// an error and let the suite run anyway.
    ///
    /// <para>It was already running out of room in production. In CIMQTT5 on main run 30847233633 it
    /// spent four of its ten attempts before Postgres answered — roughly 1.1s of a 2.5s allowance —
    /// so a slower runner would have sailed past the end and started the tests against a database
    /// that was not up. Every failure after that would have looked like a test problem.</para>
    /// </summary>
    private void WaitForDatabaseToBeReady()
    {
        awaitService("PostgreSQL", TimeSpan.FromMinutes(2), () =>
        {
            using var conn = new Npgsql.NpgsqlConnection(PostgresConnectionString + ";Pooling=false");
            conn.Open();

            var cmd = conn.CreateCommand();
            cmd.CommandText = "select 1";
            cmd.ExecuteNonQuery();

            return null;
        });
    }

    private Dictionary<string, string[]> ReferencedProjects = new()
    {
        { "jasperfx", ["JasperFx", "JasperFx.Events", "EventTests", "JasperFx.RuntimeCompiler"] },
        { "weasel", ["Weasel.Core", "Weasel.Postgresql", "Weasel.SqlServer"] },
        {"marten", ["Marten"]},
        {"polecat", ["Polecat"]}
    };

    //string[] Nugets = ["JasperFx", "JasperFx.Events", "JasperFx.RuntimeCompiler", "Weasel.Postgresql"];

    public record NugetToProjectReference(Project LocalProject, string[] NugetNames);

    private IEnumerable<NugetToProjectReference> nugetReferences()
    {
        yield return new(Solution.Wolverine, ["JasperFx", "JasperFx.RuntimeCompiler", "JasperFx.Events"]);

        yield return new(Solution.Persistence.PostgreSQL.Wolverine_Postgresql, ["Weasel.Postgresql"]);
        yield return new(Solution.Persistence.Wolverine_RDBMS, ["Weasel.Core"]);
        yield return new(Solution.Persistence.SqlServer.Wolverine_SqlServer, ["Weasel.SqlServer"]);
        yield return new(Solution.Persistence.Marten.Wolverine_Marten, ["Marten"]);
        yield return new(Solution.Persistence.Polecat.Wolverine_Polecat, ["Polecat"]);
    }
    
    Target Attach => _ => _.Executes(() =>
    {
        // Remove Nuget references FIRST
        foreach (var reference in nugetReferences())
        {
            foreach (var nugetName in reference.NugetNames)
            {
                DotNet($"remove {reference.LocalProject.Path} package {nugetName}");
            }
        }
        
        foreach (var pair in ReferencedProjects)
        {
            foreach (var projectName in pair.Value)
            {
                addProject(pair.Key, projectName);
            }
        }



        // var marten = Solution.GetProject("Marten").Path;
        // foreach (var nuget in Nugets)
        // {
        //     DotNet($"remove {marten} package {nuget}");
        // }
    });

    Target Detach => _ => _.Executes(() =>
    {
        foreach (var pair in ReferencedProjects)
        {
            foreach (var projectName in pair.Value)
            {
                removeProject(pair.Key, projectName);
            }
        }

        foreach (var reference in nugetReferences())
        {
            foreach (var nugetName in reference.NugetNames)
            {
                DotNet($"add {reference.LocalProject.Path} package {nugetName} --prerelease");
            }
        }
    });

    private void addProject(string repository, string projectName)
    {
        var path =  Path.GetFullPath($"../{repository}/src/{projectName}/{projectName}.csproj");;
        var slnPath = Solution.Path;
        DotNet($"sln {slnPath} add {path} --solution-folder Attached");

        foreach (var reference in nugetReferences())
        {
            if (reference.NugetNames.Contains(projectName))
            {
                DotNet($"add {reference.LocalProject.Path} reference {path}");
            }
        }
    }
    
    private void removeProject(string repository, string projectName)
    {
        var path =  Path.GetFullPath($"../{repository}/src/{projectName}/{projectName}.csproj");
        
        foreach (var reference in nugetReferences())
        {
            if (reference.NugetNames.Contains(projectName))
            {
                DotNet($"remove {reference.LocalProject.Path} reference {path}");
            }
        }

        var slnPath = Solution.Path;
        DotNet($"sln {slnPath} remove {path}");
        

    }

    
    
}
