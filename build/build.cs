using System;
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
        .DependsOn(FluentValidationTests, MemoryPackTests, MessagePackTests);
    
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
                Solution.Http.Wolverine_Http,
                Solution.Http.Wolverine_Http_FluentValidation,
                Solution.Http.Wolverine_Http_Marten,
                Solution.Testing.Wolverine_ComplianceTests
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

    Target ClearInlineSamples => _ => _
        .Executes(() =>
        {
            var files = Directory.GetFiles("./docs", "*.md", SearchOption.AllDirectories);
            var pattern = @"<!-- snippet:(.+)-->[\s\S]*?<!-- endSnippet -->";
            var replacePattern = $"<!-- snippet:$1-->{Environment.NewLine}<!-- endSnippet -->";
            foreach (var file in files)
            {
                // Console.WriteLine(file);
                var content = File.ReadAllText(file);

                if (!content.Contains("<!-- snippet:"))
                {
                    continue;
                }

                var updatedContent = Regex.Replace(content, pattern, replacePattern);
                File.WriteAllText(file, updatedContent);
            }
        });
    
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
}
