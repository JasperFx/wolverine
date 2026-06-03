using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
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
            RunTestProjectsOneClassAtATime(persistenceDir);
        });

    /// <summary>
    /// Determines if a project is a leader election test project,
    /// which requires running each test method individually.
    /// </summary>
    static bool IsLeaderElectionProject(string projectPath)
    {
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        return projectName.Contains("LeaderElection", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Discovers test classes from a compiled test project by running <c>dotnet test --list-tests</c>.
    /// Returns the set of fully-qualified class names that contain at least one test.
    /// This discovers inherited tests (e.g. TransportCompliance<>) that source-level regex misses.
    /// </summary>
    static HashSet<string> DiscoverTestClassesFromAssembly(string projectPath, string configuration, string frameworkOverride = null)
    {
        var args = $"test \"{projectPath}\" --no-build --list-tests --configuration {configuration}";
        if (!string.IsNullOrEmpty(frameworkOverride))
            args += $" --framework {frameworkOverride}";

        var process = ProcessTasks.StartProcess("dotnet", args, logOutput: false, logInvocation: false);
        process.AssertWaitForExit();

        var classes = new HashSet<string>();
        // Test lines are indented (start with whitespace):
        //   Namespace.ClassName.MethodName                          ([Fact])
        //   Namespace.ClassName.MethodName(param: value)            ([Theory] + [InlineData])
        // Header lines (e.g. "Test run for ...", "The following...") are not indented.
        // Class name = everything before the last dot BEFORE the first '('.
        // Truncating at '(' first avoids dots in parameter values (e.g. "http://example").
        foreach (var raw in process.Output.Select(line => line.Text))
        {
            if (raw.Length == 0 || !char.IsWhiteSpace(raw[0])) continue;  // skip header lines
            var text = raw.Trim();

            // Strip parameters before splitting: "method(param: val)" -> "method"
            var paren = text.IndexOf('(');
            var stripped = paren >= 0 ? text[..paren] : text;

            var lastDot = stripped.LastIndexOf('.');
            if (lastDot > 0)
                classes.Add(stripped[..lastDot]);
        }

        return classes;
    }

    /// <summary>
    /// Discovers individual test methods from a compiled test project by running
    /// <c>dotnet test --list-tests</c>. Returns tuples of (fully-qualified class name, method name).
    /// This discovers inherited tests (e.g. TransportCompliance<>) that source-level regex misses.
    /// Handles both [Fact] (simple method names) and [Theory] + [InlineData] (names with parameters).
    /// </summary>
    static List<(string ClassName, string MethodName)> DiscoverTestMethodsFromAssembly(string projectPath, string configuration, string frameworkOverride = null)
    {
        var args = $"test \"{projectPath}\" --no-build --list-tests --configuration {configuration}";
        if (!string.IsNullOrEmpty(frameworkOverride))
            args += $" --framework {frameworkOverride}";

        var process = ProcessTasks.StartProcess("dotnet", args, logOutput: false, logInvocation: false);
        process.AssertWaitForExit();

        var results = new List<(string, string)>();
        // Test lines are indented (start with whitespace):
        //   Namespace.ClassName.MethodName                    ([Fact])
        //   Namespace.ClassName.MethodName(param: value)      ([Theory] + [InlineData])
        // Header lines (e.g. "Test run for ...", "The following...") are not indented.
        // First strip parameters to avoid dots in param values (e.g. "http://example").
        foreach (var raw in process.Output.Select(line => line.Text))
        {
            if (raw.Length == 0 || !char.IsWhiteSpace(raw[0])) continue;
            var text = raw.Trim();

            // Strip parameters: "method(param: val)" -> "method"
            var paren = text.IndexOf('(');
            var stripped = paren >= 0 ? text[..paren] : text;

            var lastDot = stripped.LastIndexOf('.');
            if (lastDot <= 0) continue;

            var className = stripped[..lastDot];
            var methodName = stripped[(lastDot + 1)..];

            results.Add((className, methodName));
        }

        return results;
    }

    /// <summary>
    /// Runs a single dotnet test invocation with retry logic.
    /// Returns true if the test passed (on first attempt or retry).
    /// </summary>
    bool RunTestWithRetry(string projectPath, string filter, string description, int maxAttempts = 2, string frameworkOverride = null)
    {
        var framework = frameworkOverride ?? Framework;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (attempt > 1)
                {
                    Log.Warning("  Retry attempt {Attempt} for {Description}", attempt, description);
                }

                DotNetTest(c => c
                    .SetProjectFile(projectPath)
                    .SetConfiguration(Configuration)
                    .EnableNoBuild()
                    .EnableNoRestore()
                    .SetFramework(framework)
                    .SetFilter(filter));

                return true;
            }
            catch (Exception ex)
            {
                if (attempt == maxAttempts)
                {
                    Log.Error("  {Description} failed after {Attempts} attempts: {Message}",
                        description, maxAttempts, ex.Message);
                    return false;
                }

                Log.Warning("  {Description} failed on attempt {Attempt}, will retry: {Message}",
                    description, attempt, ex.Message);
            }
        }

        return false;
    }

    /// <summary>
    /// Improved test runner that discovers actual test classes from compiled assemblies,
    /// runs each class in isolation, and supports leader election one-test-at-a-time mode.
    /// Uses <c>dotnet test --list-tests</c> instead of source regex so inherited tests
    /// (e.g. TransportCompliance<>) are correctly discovered.
    /// </summary>
    void RunTestProjectsOneClassAtATime(AbsolutePath directory)
    {
        var testProjects = directory.GlobFiles("**/*Tests.csproj", "**/*Tests/*.csproj")
            .Select(p => p.ToString())
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        var failedTests = new List<string>();

        foreach (var projectPath in testProjects)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);

            if (IsLeaderElectionProject(projectPath))
            {
                // Leader election: run each test method individually
                var testMethods = DiscoverTestMethodsFromAssembly(projectPath, Configuration);
                Log.Information("Running leader election tests one method at a time for {Project} ({Count} tests)",
                    projectName, testMethods.Count);

                foreach (var (className, methodName) in testMethods)
                {
                    var filter = AppendCategoryFilter($"FullyQualifiedName~{className}.{methodName}");
                    var description = $"{projectName}/{className.Split('.')[^1]}.{methodName}";
                    Log.Information("  Running {Description}...", description);

                    if (!RunTestWithRetry(projectPath, filter, description))
                    {
                        failedTests.Add(description);
                    }
                }
            }
            else
            {
                // Normal: run one class at a time, discovered from the compiled assembly
                // so inherited tests (e.g. TransportCompliance<>) are not missed.
                var testClasses = DiscoverTestClassesFromAssembly(projectPath, Configuration);
                Log.Information("Running tests one class at a time for {Project} ({Count} classes)",
                    projectName, testClasses.Count);

                foreach (var fullClassName in testClasses)
                {
                    var shortName = fullClassName.Split('.')[^1];
                    var filter = AppendCategoryFilter($"FullyQualifiedName~{fullClassName}.");
                    var description = $"{projectName}/{shortName}";
                    Log.Information("  Running {Description}...", description);

                    if (!RunTestWithRetry(projectPath, filter, description))
                    {
                        failedTests.Add(description);
                    }
                }
            }
        }

        if (failedTests.Any())
        {
            Log.Error("The following tests failed after retries:");
            foreach (var test in failedTests)
            {
                Log.Error("  - {Test}", test);
            }
        }
    }

    /// <summary>
    /// Appends Category!=Flaky to a test filter when running in CI.
    /// </summary>
    static string AppendCategoryFilter(string filter)
    {
        return filter + "&Category!=Flaky";
    }

    /// <summary>
    /// Runs an entire test project in a single <c>dotnet test</c> invocation,
    /// retrying the whole project once on failure. Execution stays serial — the
    /// project's <c>[assembly: CollectionBehavior(CollectionPerAssembly)]</c>
    /// (e.g. MartenTests/NoParallelization.cs) keeps every test class in one
    /// collection, so there's no concurrency and therefore no shared-schema /
    /// shared-database collision risk. The win over
    /// <see cref="RunSingleProjectOneClassAtATime"/> is process count: one
    /// <c>dotnet test</c> spawn instead of one-per-class (111 for MartenTests),
    /// eliminating the per-class process-start + assembly-load + xUnit-discovery
    /// overhead that dominates the wall clock on the slow persistence jobs.
    ///
    /// Tradeoff vs. one-class-at-a-time: per-class retry granularity is lost
    /// (a failure re-runs the whole project), and all classes share one process
    /// (a hung daemon / leaked connection in one class can affect another rather
    /// than being isolated to its own process). Used where the spawn overhead
    /// outweighs those — see #2810.
    /// </summary>
    void RunWholeProjectWithRetry(string projectPath, string frameworkOverride = null)
    {
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        Log.Information("Running entire project {Project} in a single invocation (see #2810)", projectName);

        // No FullyQualifiedName filter — run the whole assembly. Still exclude
        // Flaky-tagged tests, matching the one-class-at-a-time path's filter.
        if (!RunTestWithRetry(projectPath, "Category!=Flaky", projectName, frameworkOverride: frameworkOverride))
        {
            throw new Exception($"Tests failed in {projectName}");
        }
    }

    /// <summary>
    /// Runs a single test project one class at a time with retry logic.
    /// Used by individual Nuke targets for specific test projects.
    /// </summary>
    void RunSingleProjectOneClassAtATime(string projectPath, string frameworkOverride = null)
    {
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var failedTests = new List<string>();

        if (IsLeaderElectionProject(projectPath))
        {
            var testMethods = DiscoverTestMethodsFromAssembly(projectPath, Configuration, frameworkOverride);
            Log.Information("Running leader election tests one method at a time for {Project} ({Count} tests)",
                projectName, testMethods.Count);

            foreach (var (className, methodName) in testMethods)
            {
                var filter = AppendCategoryFilter($"FullyQualifiedName~{className}.{methodName}");
                var description = $"{projectName}/{className.Split('.')[^1]}.{methodName}";
                Log.Information("  Running {Description}...", description);

                if (!RunTestWithRetry(projectPath, filter, description, frameworkOverride: frameworkOverride))
                {
                    failedTests.Add(description);
                }
            }
        }
        else
        {
            // Normal: run one class at a time, discovered from the compiled assembly
            // so inherited tests (e.g. TransportCompliance<>) are not missed.
            var testClasses = DiscoverTestClassesFromAssembly(projectPath, Configuration, frameworkOverride);
            Log.Information("Running tests one class at a time for {Project} ({Count} classes)",
                projectName, testClasses.Count);

            foreach (var fullClassName in testClasses)
            {
                var shortName = fullClassName.Split('.')[^1];
                var filter = AppendCategoryFilter($"FullyQualifiedName~{fullClassName}.");
                var description = $"{projectName}/{shortName}";
                Log.Information("  Running {Description}...", description);

                if (!RunTestWithRetry(projectPath, filter, description, frameworkOverride: frameworkOverride))
                {
                    failedTests.Add(description);
                }
            }
        }

        if (failedTests.Any())
        {
            Log.Error("The following tests failed after retries:");
            foreach (var test in failedTests)
            {
                Log.Error("  - {Test}", test);
            }

            throw new Exception($"{failedTests.Count} test(s) failed in {projectName}");
        }
    }
}