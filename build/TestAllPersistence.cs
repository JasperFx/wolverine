using System;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    Target TestAllPersistence => _ => _
        .DependsOn(Compile, DockerUp)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var persistenceDir = RootDirectory / "src" / "Persistence";
            RunTestProjectsOneFileAtATime(persistenceDir);
        });

    void RunTestProjectsOneFileAtATime(AbsolutePath directory)
    {
        var testProjects = directory.GlobFiles("**/*Tests.csproj", "**/*Tests/*.csproj")
            .Select(p => p.ToString())
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        foreach (var projectPath in testProjects)
        {
            var projectDir = Path.GetDirectoryName(projectPath);
            var testFiles = Directory.GetFiles(projectDir!, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .OrderBy(f => f)
                .ToList();

            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            Log.Information("Running tests one file at a time for {Project} ({Count} files)", projectName, testFiles.Count);

            foreach (var testFile in testFiles)
            {
                var className = Path.GetFileNameWithoutExtension(testFile);
                Log.Information("  Running tests in {ClassName}...", className);

                try
                {
                    DotNetTest(c => c
                        .SetProjectFile(projectPath)
                        .SetConfiguration(Configuration)
                        .EnableNoBuild()
                        .EnableNoRestore()
                        .SetFramework(Framework)
                        .SetFilter($"FullyQualifiedName~{className}"));
                }
                catch (Exception ex)
                {
                    Log.Error("  Tests in {ClassName} failed: {Message}", className, ex.Message);
                }
            }
        }
    }
}
