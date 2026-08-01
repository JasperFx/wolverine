using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;

partial class Build
{
    Target TestAllPersistence => _ => _
        .DependsOn(Compile, DockerUp)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var persistenceDir = RootDirectory / "src" / "Persistence";
            RunAllTestsProjects(persistenceDir);
        });

    /// <summary>
    /// Runs all test projects under a directory through the supervised runner
    /// (see SupervisedTests.cs).
    /// </summary>
    void RunAllTestsProjects(AbsolutePath directory)
    {
        var testProjects = directory.GlobFiles("**/*Tests.csproj", "**/*Tests/*.csproj")
            .Select(p => p.ToString())
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        RunTestProjects([..testProjects]);
    }
}
