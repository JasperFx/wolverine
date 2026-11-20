using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;

partial class Build
{
    Target TestAllPersistence => _ => _
        .DependsOn(Compile, FreshContainers, DockerUp)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var persistenceDir = RootDirectory / "src" / "Persistence";
            // GH-5545: this run started SQL Server fresh, so it may recreate it when a suite degrades on it
            RunAllTestsProjects(persistenceDir, heal: ["sqlserver"]);
        });

    /// <summary>
    /// Runs all test projects under a directory through the supervised runner
    /// (see SupervisedTests.cs).
    /// </summary>
    void RunAllTestsProjects(AbsolutePath directory, string[] heal = null)
    {
        var testProjects = directory.GlobFiles("**/*Tests.csproj", "**/*Tests/*.csproj")
            .Select(p => p.ToString())
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        RunTestProjects([..testProjects], heal: heal);
    }
}
