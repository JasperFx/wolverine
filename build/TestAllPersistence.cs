using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
            RunTestProjectsOneClassAtATime(persistenceDir);
        });

    /// <summary>
    /// Files that are never test classes and should be skipped during discovery.
    /// </summary>
    static readonly HashSet<string> SkippedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "NoParallelization",
        "GlobalUsings",
        "AssemblyInfo",
        "Usings",
        "ModuleInitializer",
    };

    /// <summary>
    /// Regex patterns that indicate a file contains xUnit test methods.
    /// </summary>
    static readonly Regex TestAttributePattern = new(
        @"\[\s*(Fact|Theory|InlineData|MemberData|ClassData)",
        RegexOptions.Compiled);

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
    /// Discovers test classes from source files by looking for [Fact] or [Theory] attributes.
    /// Returns the class name (file name without extension) for files that contain tests.
    /// </summary>
    static List<string> DiscoverTestClasses(string projectDir)
    {
        var testFiles = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f)
            .ToList();

        var testClasses = new List<string>();
        foreach (var testFile in testFiles)
        {
            var className = Path.GetFileNameWithoutExtension(testFile);

            if (SkippedFileNames.Contains(className))
                continue;

            try
            {
                var content = File.ReadAllText(testFile);
                if (TestAttributePattern.IsMatch(content))
                {
                    testClasses.Add(className);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Could not read {File}: {Message}", testFile, ex.Message);
            }
        }

        return testClasses;
    }

    /// <summary>
    /// Discovers individual test method names from source files for leader election projects.
    /// Returns tuples of (className, methodName). Also follows class inheritance into
    /// compliance base classes in src/Testing/Wolverine.ComplianceTests/ so that inherited
    /// [Fact]/[Theory] methods are attributed to the concrete class.
    /// </summary>
    static List<(string ClassName, string MethodName)> DiscoverTestMethods(string projectDir)
    {
        var methodPattern = new Regex(
            @"\[\s*(?:Fact|Theory).*?\]\s*(?:\[.*?\]\s*)*public\s+(?:async\s+)?(?:Task|void)\s+(\w+)\s*\(",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var classDeclarationPattern = new Regex(
            @"(?:public\s+|internal\s+|abstract\s+|sealed\s+)*class\s+(\w+)\s*(?::\s*([\w<>,\s\.]+?))?\s*(?:\{|where\b)",
            RegexOptions.Compiled);

        var complianceBaseDir = FindComplianceTestsDir(projectDir);

        var testFiles = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        var results = new List<(string, string)>();
        var seen = new HashSet<(string, string)>();

        void AddMethod(string className, string methodName)
        {
            if (seen.Add((className, methodName)))
            {
                results.Add((className, methodName));
            }
        }

        foreach (var testFile in testFiles)
        {
            var className = Path.GetFileNameWithoutExtension(testFile);
            if (SkippedFileNames.Contains(className)) continue;

            try
            {
                var content = File.ReadAllText(testFile);

                foreach (Match match in methodPattern.Matches(content))
                {
                    AddMethod(className, match.Groups[1].Value);
                }

                if (complianceBaseDir == null) continue;

                foreach (Match classMatch in classDeclarationPattern.Matches(content))
                {
                    var baseList = classMatch.Groups[2].Value;
                    if (string.IsNullOrWhiteSpace(baseList)) continue;

                    var concreteClassName = classMatch.Groups[1].Value;
                    var baseTypeName = baseList.Split(',')[0].Trim().Split('<')[0].Trim();
                    if (string.IsNullOrWhiteSpace(baseTypeName)) continue;

                    var baseFile = Path.Combine(complianceBaseDir, baseTypeName + ".cs");
                    if (!File.Exists(baseFile)) continue;

                    try
                    {
                        var baseContent = File.ReadAllText(baseFile);
                        foreach (Match baseMatch in methodPattern.Matches(baseContent))
                        {
                            AddMethod(concreteClassName, baseMatch.Groups[1].Value);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("Could not read compliance base {File}: {Message}", baseFile, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Could not read {File}: {Message}", testFile, ex.Message);
            }
        }

        return results;
    }

    /// <summary>
    /// Walks up from the project directory to locate src/Testing/Wolverine.ComplianceTests/
    /// so inherited compliance tests can be discovered.
    /// </summary>
    static string? FindComplianceTestsDir(string projectDir)
    {
        var current = new DirectoryInfo(projectDir);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "src", "Testing", "Wolverine.ComplianceTests");
            if (Directory.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        return null;
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
    /// Improved test runner that discovers actual test classes from source,
    /// runs each class in isolation, and supports leader election one-test-at-a-time mode.
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
            var projectDir = Path.GetDirectoryName(projectPath)!;
            var projectName = Path.GetFileNameWithoutExtension(projectPath);

            if (IsLeaderElectionProject(projectPath))
            {
                // Leader election: run each test method individually
                var testMethods = DiscoverTestMethods(projectDir);
                Log.Information("Running leader election tests one method at a time for {Project} ({Count} tests)",
                    projectName, testMethods.Count);

                foreach (var (className, methodName) in testMethods)
                {
                    var filter = $"FullyQualifiedName~{className}.{methodName}";
                    var description = $"{projectName}/{className}.{methodName}";
                    Log.Information("  Running {Description}...", description);

                    if (!RunTestWithRetry(projectPath, filter, description))
                    {
                        failedTests.Add(description);
                    }
                }
            }
            else
            {
                // Normal: run one class at a time
                var testClasses = DiscoverTestClasses(projectDir);
                Log.Information("Running tests one class at a time for {Project} ({Count} classes)",
                    projectName, testClasses.Count);

                foreach (var className in testClasses)
                {
                    var filter = AppendCategoryFilter($"FullyQualifiedName~{className}");
                    var description = $"{projectName}/{className}";
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
    /// Runs a single test project one class at a time with retry logic.
    /// Used by individual Nuke targets for specific test projects.
    /// </summary>
    void RunSingleProjectOneClassAtATime(string projectPath, string frameworkOverride = null)
    {
        var projectDir = Path.GetDirectoryName(projectPath)!;
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var failedTests = new List<string>();

        if (IsLeaderElectionProject(projectPath))
        {
            var testMethods = DiscoverTestMethods(projectDir);
            Log.Information("Running leader election tests one method at a time for {Project} ({Count} tests)",
                projectName, testMethods.Count);

            foreach (var (className, methodName) in testMethods)
            {
                var filter = AppendCategoryFilter($"FullyQualifiedName~{className}.{methodName}");
                var description = $"{projectName}/{className}.{methodName}";
                Log.Information("  Running {Description}...", description);

                if (!RunTestWithRetry(projectPath, filter, description, frameworkOverride: frameworkOverride))
                {
                    failedTests.Add(description);
                }
            }
        }
        else
        {
            var testClasses = DiscoverTestClasses(projectDir);
            Log.Information("Running tests one class at a time for {Project} ({Count} classes)",
                projectName, testClasses.Count);

            foreach (var className in testClasses)
            {
                var filter = AppendCategoryFilter($"FullyQualifiedName~{className}");
                var description = $"{projectName}/{className}";
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
