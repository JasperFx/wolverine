using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using IntegrationTests;
using Npgsql;
using static System.Globalization.CultureInfo;
using static Bullseye.Targets;
using static SimpleExec.Command;

namespace build
{
    internal class Build
    {
        private const string BUILD_VERSION = "6.0.0";
        private const string GITHUB_REPO = "https://github.com/jasperfx/jasperfx.github.io.git";

        private static void Main(string[] args)
        {

            Target("default", DependsOn("test", "commands"));

            Target("restore", () =>
            {
                Run("dotnet", "restore wolverine.sln");
            });

            Target("compile",  DependsOn("restore"),() =>
            {
                Run("dotnet",
                    $"build wolverine.sln --no-restore");
            });
            
            Target("test", DependsOn("compile"),() =>
            {
                RunTests("CoreTests");
            });
            
            Target("test-persistence", DependsOn("compile", "docker-up"), () =>
            {
                RunTests("Persistence", "PersistenceTests");
            });
            
            Target("test-rabbit", DependsOn("compile", "docker-up"), () =>
            {
                RunTests("Transports", "Wolverine.RabbitMQ.Tests");
            });
            
            Target("test-pulsar", DependsOn("compile", "docker-up"), () =>
            {
                RunTests("Transports", "Wolverine.Pulsar.Tests");
            });
            
            Target("full", DependsOn("test", "test-persistence", "test-rabbit", "test-pulsar"), () =>
            {
                Console.WriteLine("Look Ma, I'm running full!");
            });

            Target("commands", DependsOn("compile"),() =>
            {
                var original = Directory.GetCurrentDirectory();

                /*
  Dir.chdir("src/Subscriber") do
    sh "dotnet run -- ?"
    sh "dotnet run -- export-json-schema obj/schema"
  end
                 */

                Directory.SetCurrentDirectory(Path.Combine(original, "src", "ConsoleApp"));
                RunCurrentProject("?");
                RunCurrentProject("export-json-schema obj/schema");
                RunCurrentProject("describe");


                Directory.SetCurrentDirectory(original);
            });

            Target("ci", DependsOn("compile"));

            Target("install", () =>
                RunNpm("install"));

            Target("install-mdsnippets", IgnoreIfFailed(() =>
                Run("dotnet", $"tool install -g MarkdownSnippets.Tool")
            ));

            Target("docs", DependsOn("install", "install-mdsnippets"), () => {
                // Run docs site
                RunNpm("run docs");
            });

            Target("docs-build", DependsOn("install", "install-mdsnippets"), () => {
                // Run docs site
                RunNpm("run docs:build");
            });

            Target("publish-docs", DependsOn("docs-build"), () =>
                Run("npm", "run docs:publish"));

            Target("clear-inline-samples", () => {
                var files = Directory.GetFiles("./docs", "*.md", SearchOption.AllDirectories);
                var pattern = @"<!-- snippet:(.+)-->[\s\S]*?<!-- endSnippet -->";
                var replacePattern = $"<!-- snippet:$1-->{Environment.NewLine}<!-- endSnippet -->";
                foreach (var file in files)
                {
                    // Console.WriteLine(file);
                    var content = File.ReadAllText(file);

                    if (!content.Contains("<!-- snippet:")) {
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

            var nugetProjects = new string[]{
                "./src/Wolverine", 
                "./src/Transports/Wolverine.RabbitMQ", 
                "./src/Transports/Wolverine.AzureServiceBus", 
                "./src/Transports/Wolverine.AmazonSqs", 
                "./src/Persistence/Wolverine.RDBMS", 
                "./src/Persistence/Wolverine.Postgresql", 
                "./src/Persistence/Wolverine.EntityFrameworkCore", 
                "./src/Persistence/Wolverine.Marten",
                "./src/Persistence/Wolverine.SqlServer",
                "./src/Extensions/Wolverine.FluentValidation",
                "./src/Extensions/Wolverine.MemoryPack"
            };

            Target("pack", DependsOn("compile"), ForEach(nugetProjects), project =>
                Run("dotnet", $"pack {project} -o ./artifacts --configuration Release"));


            RunTargetsAndExit(args);
        }

        private static void WaitForDatabaseToBeReady()
        {
            var attempt = 0;
            while (attempt < 10)
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


        private static void RunCurrentProject(string args)
        {
            Run("dotnet", $"run  --framework net6.0 --no-build --no-restore -- {args}");
        }

        private static void CopyFilesRecursively(string sourcePath, string targetPath)
        {
            //Now Create all of the directories
            foreach (string dirPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(sourcePath, targetPath));
            }

            //Copy all the files & Replaces any files with the same name
            foreach (string newPath in Directory.GetFiles(sourcePath, "*.*",SearchOption.AllDirectories))
            {
                File.Copy(newPath, newPath.Replace(sourcePath, targetPath), true);
            }
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

        private static void RunNpm(string args) =>
            Run("npm", args, windowsName: "cmd.exe", windowsArgs: $"/c npm {args}");

        private static void RunTests(params string[] paths)
        {
            var projectName = paths.Last();

            var path = paths.Length == 1
                ? $"src/Testing/{projectName}/{projectName}.csproj"
                : $"src/{string.Join('/', paths.SkipLast(1))}/{projectName}/{projectName}.csproj";
            
            Run("dotnet", $"test --no-build --no-restore " + path);
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
            var version = float.Parse(frameworkName.Split('=')[1].Replace("v",""), InvariantCulture.NumberFormat);

            return version < 5.0 ? $"netcoreapp{version.ToString("N1", InvariantCulture.NumberFormat)}" : $"net{version.ToString("N1", InvariantCulture.NumberFormat)}";
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
}
