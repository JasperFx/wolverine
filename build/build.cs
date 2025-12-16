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

class Build : NukeBuild
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
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .SetFramework(Framework)
                .EnableNoRestore());
        });

    Target CI => _ => _
        .DependsOn(CoreTests);

    Target Test => _ => _
        .DependsOn(CoreTests, TestExtensions, Commands, PolicyTests, HttpTests);

    Target Full => _ => _
        .DependsOn(Test, PersistenceTests, RabbitmqTests, PulsarTests);

    Target CoreTests => _ => _
        .DependsOn(Compile)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.Testing.CoreTests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework));
        });
   
    Target PolicyTests => _ => _
        .DependsOn(Compile, DockerUp)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.Testing.PolicyTests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework));
        });

    Target TestExtensions => _ => _
        .DependsOn(FluentValidationTests, DataAnnotationsValidationTests, MemoryPackTests, MessagePackTests);
    
    Target FluentValidationTests => _ => _
        .DependsOn(Compile)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.Extensions.Wolverine_FluentValidation_Tests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework));
        });

    Target DataAnnotationsValidationTests => _ => _
        .DependsOn(Compile)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.Extensions.Wolverine_DataAnnotationsValidation_Tests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework));
        });

    Target MemoryPackTests => _ => _
        .DependsOn(Compile)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.Extensions.Wolverine_MemoryPack_Tests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework));
        });
    
    Target MessagePackTests => _ => _
        .DependsOn(Compile)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.Extensions.Wolverine_MessagePack_Tests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework));
        });

    Target HttpTests => _ => _
        .DependsOn(CoreHttpTests, DataAnnotationsValidationHttpTests);

    Target CoreHttpTests => _ => _
        .DependsOn(Compile, DockerUp)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.Http.Wolverine_Http_Tests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework));
        });

    Target DataAnnotationsValidationHttpTests => _ => _
        .DependsOn(Compile)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.Http.Wolverine_Http_DataAnnotationsValidation_Tests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework));
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
    
    Target PersistenceTests => _ => _
        .DependsOn(Compile, DockerUp)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.Persistence.PersistenceTests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework));
        });
    
    Target RabbitmqTests => _ => _
        .DependsOn(Compile, DockerUp)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.Transports.RabbitMQ.Wolverine_RabbitMQ_Tests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework));
        });
    
    Target PulsarTests => _ => _
        .DependsOn(Compile, DockerUp)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.Transports.Pulsar.Wolverine_Pulsar_Tests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework));
        });

    Target TestSamples => _ => _
        .DependsOn(TodoWebServiceSampleTests, BankingServiceSampleTests, 
            AppWithMiddlewareSampleTests, ItemServiceSampleTests);
   
    Target TodoWebServiceSampleTests => _ => _
        .DependsOn(Compile, DockerUp)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.Samples.TodoWebService.TodoWebServiceTests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework));
        });
   
    Target BankingServiceSampleTests => _ => _
        .DependsOn(Compile, DockerUp)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.Samples.TestHarness.BankingService_Tests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework));
        });

    Target AppWithMiddlewareSampleTests => _ => _
        .DependsOn(Compile, DockerUp)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.Samples.Middleware.AppWithMiddleware_Tests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework));
        });

    Target ItemServiceSampleTests => _ => _
        .DependsOn(Compile, DockerUp)    
        .ProceedAfterFailure()
        .Executes(() =>
        {
            DotNetTest(c => c
                .SetProjectFile(Solution.Samples.EFCoreSample.ItemService_Tests)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(Framework));
        });

    Target Pack => _ => _
        .Executes(() =>
        {
            var nugetProjects = new[]
            {
                Solution.Wolverine,
                Solution.Transports.RabbitMQ.Wolverine_RabbitMQ,
                Solution.Transports.Azure.Wolverine_AzureServiceBus,
                Solution.Transports.AWS.Wolverine_AmazonSqs,
                Solution.Transports.AWS.Wolverine_AmazonSns,
                Solution.Transports.MQTT.Wolverine_MQTT,
                Solution.Transports.Kafka.Wolverine_Kafka,
                Solution.Transports.Pulsar.Wolverine_Pulsar,
                Solution.Transports.GCP.Wolverine_Pubsub,
                Solution.Persistence.Wolverine_RDBMS,
                Solution.Persistence.Wolverine_Postgresql,
                Solution.Persistence.Wolverine_EntityFrameworkCore,
                Solution.Persistence.Wolverine_Marten,
                Solution.Persistence.Wolverine_RavenDb,
                Solution.Persistence.Wolverine_SqlServer,
                Solution.Extensions.Wolverine_FluentValidation,
                Solution.Extensions.Wolverine_MemoryPack,
                Solution.Extensions.Wolverine_MessagePack,
                Solution.Extensions.Wolverine_Protobuf,
                Solution.Http.Wolverine_Http,
                Solution.Http.Wolverine_Http_FluentValidation,
                Solution.Http.Wolverine_Http_Marten,
                Solution.Testing.Wolverine_ComplianceTests,
                Solution.Transports.Redis.Wolverine_Redis,
                Solution.Transports.SignalR.Wolverine_SignalR
            };

            foreach (var project in nugetProjects)
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
            ProcessTasks
                .StartProcess("docker", "compose up -d", logOutput: false)
                .AssertWaitForExit()
                .AssertZeroExitCode();
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

    private void WaitForDatabaseToBeReady()
    {
        var attempt = 0;
        while (attempt < 10)
            try
            {
                using (var conn = new Npgsql.NpgsqlConnection(PostgresConnectionString + ";Pooling=false"))
                {
                    conn.Open();

                    var cmd = conn.CreateCommand();
                    cmd.CommandText = "select 1";
                    cmd.ExecuteNonQuery();

                    Log.Information("Postgresql is up and ready!");
                    break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error while waiting for the database to be ready");
                Thread.Sleep(250);
                attempt++;
            }
    }
    
    
    private Dictionary<string, string[]> ReferencedProjects = new()
    {
        { "jasperfx", ["JasperFx", "JasperFx.Events", "EventTests", "JasperFx.RuntimeCompiler"] },
        { "weasel", ["Weasel.Core", "Weasel.Postgresql", "Weasel.SqlServer"] },
        {"lamar", ["Lamar", "Lamar.Microsoft.DependencyInjection"]},
        {"marten", ["Marten"]}
    };

    //string[] Nugets = ["JasperFx", "JasperFx.Events", "JasperFx.RuntimeCompiler", "Weasel.Postgresql"];

    public record NugetToProjectReference(Project LocalProject, string[] NugetNames);

    private IEnumerable<NugetToProjectReference> nugetReferences()
    {
        yield return new(Solution.Wolverine, ["JasperFx", "JasperFx.RuntimeCompiler", "JasperFx.Events"]);
        
        yield return new(Solution.Persistence.Wolverine_Postgresql, ["Weasel.Postgresql"]);
        yield return new(Solution.Persistence.Wolverine_RDBMS, ["Weasel.Core"]);
        yield return new(Solution.Persistence.Wolverine_SqlServer, ["Weasel.SqlServer"]);
        yield return new(Solution.Persistence.Wolverine_Marten, ["Marten"]);
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