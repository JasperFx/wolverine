using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using IntegrationTests;
using Npgsql;
using static System.Globalization.CultureInfo;
using static Bullseye.Targets;
using static SimpleExec.Command;

namespace build;

internal class Build
{
    private static async Task Main(string[] args)
    {
        Target("default", DependsOn("test-core", "test-policy", "test-extensions", "test-http", "commands"));

        Target("restore", () => { Run("dotnet", "restore wolverine.sln"); });

        Target("compile", DependsOn("restore"), () =>
        {
            Run("dotnet",
                "build wolverine.sln --no-restore --framework net9.0");
        });

        Target("clean", () => { Run("dotnet", "clean wolverine.sln --framework net9.0"); });

        TestTarget("test-core", "CoreTests");
        TestTarget("test-policy", "PolicyTests");

        Target("test-extensions", DependsOn("compile"), () =>
        {
            RunTests("Extensions", "Wolverine.FluentValidation.Te" +
                                   "sts");
            RunTests("Extensions", "Wolverine.MemoryPack.Tests");
            RunTests("Extensions", "Wolverine.MessagePack.Tests");
        });

        IntegrationTestTarget("test-http", "Http", "Wolverine.Http.Tests");

        IntegrationTestTarget("test-persistence", "Persistence", "PersistenceTests");

        IntegrationTestTarget("test-rabbit", "Transports", "RabbitMQ", "Wolverine.RabbitMQ.Tests");

        IntegrationTestTarget("test-pulsar", "Transports", "Wolverine.Pulsar.Tests");

        Target("test-samples", DependsOn("compile", "docker-up"), () =>
        {
            RunTests("samples", "TodoWebService", "TodoWebServiceTests");
            RunTests("samples", "TestHarness", "BankingService.Tests");
            RunTests("samples", "Middleware", "AppWithMiddleware.Tests");
            RunTests("samples", "EFCoreSample", "ItemService.Tests");
        });

        Target("full", DependsOn("default", "test-persistence", "test-rabbit", "test-pulsar"),
            () => { Console.WriteLine("Look Ma, I'm running full!"); });

        Target("commands", DependsOn("compile"), () =>
        {
            var original = Directory.GetCurrentDirectory();

            /*
Dir.chdir("src/Subscriber") do
sh "dotnet run -- ?"
sh "dotnet run -- export-json-schema obj/schema"
end
             */

            Directory.SetCurrentDirectory(Path.Combine(original, "src", "Testing", "ConsoleApp"));
            RunCurrentProject("?");
            RunCurrentProject("describe");

            Directory.SetCurrentDirectory(Path.Combine(original, "src", "Http", "WolverineWebApi"));
            RunCurrentProject("codegen preview");

            Directory.SetCurrentDirectory(original);
        });

        Target("ci", DependsOn("test-core"));

        Target("install", () =>
            RunNpm("install"));

        Target("install-mdsnippets", IgnoreIfFailed(() =>
            Run("dotnet", "tool install -g MarkdownSnippets.Tool")
        ));

        Target("docs", DependsOn("install", "install-mdsnippets"), () =>
        {
            // Run docs site
            RunNpm("run docs");
        });

        Target("docs-build", DependsOn("install", "install-mdsnippets"), () =>
        {
            // Run docs site
            RunNpm("run docs:build");
        });

        Target("publish-docs", DependsOn("docs-build"), () =>
            Run("npm", "run docs:publish"));

        Target("clear-inline-samples", () =>
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


        Target("docker-up", () =>
        {
            Run("docker", "compose up -d");

            WaitForDatabaseToBeReady();
        });

        var nugetProjects = new[]
        {
            "./src/Wolverine",
            "./src/Transports/RabbitMQ/Wolverine.RabbitMQ",
            "./src/Transports/Azure/Wolverine.AzureServiceBus",
            "./src/Transports/AWS/Wolverine.AmazonSqs",
            "./src/Transports/MQTT/Wolverine.MQTT",
            "./src/Transports/Kafka/Wolverine.Kafka",
            "./src/Transports/Pulsar/Wolverine.Pulsar",
            "./src/Transports/GCP/Wolverine.Pubsub",
            "./src/Persistence/Wolverine.RDBMS",
            "./src/Persistence/Wolverine.Postgresql",
            "./src/Persistence/Wolverine.EntityFrameworkCore",
            "./src/Persistence/Wolverine.Marten",
            "./src/Persistence/Wolverine.RavenDb",
            "./src/Persistence/Wolverine.SqlServer",
            "./src/Extensions/Wolverine.FluentValidation",
            "./src/Extensions/Wolverine.MemoryPack",
            "./src/Extensions/Wolverine.MessagePack",
            "./src/Http/Wolverine.Http",
            "./src/Http/Wolverine.Http.FluentValidation",
            "./src/Http/Wolverine.Http.Marten",
            "./src/Testing/Wolverine.ComplianceTests"
        };

        Target("pack", ForEach(nugetProjects), project =>
            Run("dotnet", $"pack {project} -o ./artifacts --configuration Release"));


        await RunTargetsAndExitAsync(args);
    }

    private static void WaitForDatabaseToBeReady()
    {
        var attempt = 0;
        while (attempt < 10)
        {
            try
            {
                using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
                conn.Open();

                var cmd = conn.CreateCommand();
                cmd.CommandText = "select 1";
                cmd.ExecuteReader();

                Console.WriteLine("Postgresql is up and ready!");
                break;
            }
            catch (Exception)
            {
                Thread.Sleep(250);
                attempt++;
            }
        }
    }

    public static void TestTarget(string taskName, params string[] testTarget)
    {
        Target(taskName, DependsOn("compile"), () => { RunTests(testTarget); });
    }

    public static void IntegrationTestTarget(string taskName, params string[] testTarget)
    {
        Target(taskName, DependsOn("compile", "docker-up"), () => { RunTests(testTarget); });
    }


    private static void RunCurrentProject(string args)
    {
        Run("dotnet", $"run  --framework net9.0 --no-build --no-restore -- {args}");
    }

    private static void CopyFilesRecursively(string sourcePath, string targetPath)
    {
        //Now Create all of the directories
        foreach (var dirPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dirPath.Replace(sourcePath, targetPath));

        //Copy all the files & Replaces any files with the same name
        foreach (var newPath in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories))
            File.Copy(newPath, newPath.Replace(sourcePath, targetPath), true);
    }

    private static string InitializeDirectory(string path)
    {
        EnsureDirectoriesDeleted(path);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void EnsureDirectoriesDeleted(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var dir = new DirectoryInfo(path);
                DeleteDirectory(dir);
            }
        }
    }

    private static void DeleteDirectory(DirectoryInfo baseDir)
    {
        baseDir.Attributes = FileAttributes.Normal;
        foreach (var childDir in baseDir.GetDirectories())
            DeleteDirectory(childDir);

        foreach (var file in baseDir.GetFiles())
            file.IsReadOnly = false;

        baseDir.Delete(true);
    }

    private static void RunNpm(string args)
    {
        Run("npm", args);
    }

    private static void RunTests(params string[] paths)
    {
        var projectName = paths.Last();

        var path = paths.Length == 1
            ? $"src/Testing/{projectName}/{projectName}.csproj"
            : $"src/{string.Join('/', paths.SkipLast(1))}/{projectName}/{projectName}.csproj";

        if (!string.IsNullOrEmpty(GetEnvironmentVariable("GITHUB_ACTIONS")))
        {
            path += " --logger \"GitHubActions;verbosity=detailed\"";
        }


        Run("dotnet", "test --no-build --no-restore --logger GitHubActions --framework net9.0 " + path);
    }

    private static string GetEnvironmentVariable(string variableName)
    {
        var val = Environment.GetEnvironmentVariable(variableName);

        // Azure devops converts environment variable to upper case and dot to underscore
        // https://docs.microsoft.com/en-us/azure/devops/pipelines/process/variables?view=azure-devops&tabs=yaml%2Cbatch
        // Attempt to fetch variable by updating it
        if (string.IsNullOrEmpty(val))
        {
            val = Environment.GetEnvironmentVariable(variableName.ToUpper().Replace(".", "_"));
        }

        Console.WriteLine(val);

        return val;
    }

    private static string GetFramework()
    {
        var frameworkName = Assembly.GetEntryAssembly().GetCustomAttribute<TargetFrameworkAttribute>().FrameworkName;
        var version = float.Parse(frameworkName.Split('=')[1].Replace("v", ""), InvariantCulture.NumberFormat);

        return version < 5.0
            ? $"netcoreapp{version.ToString("N1", InvariantCulture.NumberFormat)}"
            : $"net{version.ToString("N1", InvariantCulture.NumberFormat)}";
    }

    private static Action IgnoreIfFailed(Action action)
    {
        return () =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Console.WriteLine(exception.Message);
            }
        };
    }
}