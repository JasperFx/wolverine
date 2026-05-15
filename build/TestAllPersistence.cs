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
    static string FindComplianceTestsDir(string projectDir)
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
                // Normal: batch test classes per project (#2810). Batch retry; on
                // batch failure, fall back to per-class for accurate isolation.
                var testClasses = DiscoverTestClasses(projectDir);
                failedTests.AddRange(RunTestsInBatches(projectPath, projectName, testClasses));
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
    /// How many test classes to bundle into one <c>dotnet test</c> invocation.
    /// Drives the wall-clock optimization in #2810: spawning <c>dotnet test</c>
    /// once per class against a 100-class project (MartenTests has 111) means
    /// the process spawn + fixture init dominates wall-clock. Batching cuts
    /// invocations from N to ~N/<see cref="TestBatchSize"/> at the cost of
    /// losing per-class retry granularity inside a batch.
    ///
    /// Override at the workflow level via the <c>WOLVERINE_TEST_BATCH_SIZE</c>
    /// environment variable. Default 10 picked empirically: cuts MartenTests
    /// invocations from 111 to ~11 while keeping each batch small enough that
    /// fixture state poisoning stays localised.
    ///
    /// The failure path falls back to per-class invocation (see
    /// <see cref="RunTestsInBatches"/>) so retry+isolation semantics are
    /// preserved exactly where they matter — when something actually fails.
    /// </summary>
    static int TestBatchSize
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("WOLVERINE_TEST_BATCH_SIZE");
            if (int.TryParse(raw, out var value) && value > 0) return value;
            return 10;
        }
    }

    /// <summary>
    /// Runs the supplied test classes in batches (one <c>dotnet test</c> call
    /// per batch) with a per-batch retry. On a batch that fails after retry,
    /// falls back to running each class in that batch individually with the
    /// per-class retry shape that <c>RunSingleProjectOneClassAtATime</c> used
    /// before #2810 — so an isolated flake in one class only re-runs that
    /// class, not the whole batch.
    ///
    /// Returns the list of class descriptions that still failed after the
    /// fallback path. Empty list = success.
    /// </summary>
    /// <param name="projectPath">Path to the test csproj.</param>
    /// <param name="projectName">Friendly project name for log lines.</param>
    /// <param name="testClasses">Discovered test class names.</param>
    /// <param name="frameworkOverride">Optional TFM override.</param>
    List<string> RunTestsInBatches(string projectPath, string projectName,
        IReadOnlyList<string> testClasses, string frameworkOverride = null)
    {
        var failedTests = new List<string>();
        var batchSize = TestBatchSize;
        var batches = testClasses
            .Select((cls, idx) => new { cls, idx })
            .GroupBy(x => x.idx / batchSize)
            .Select(g => g.Select(x => x.cls).ToList())
            .ToList();

        Log.Information("Running {Total} test classes in {BatchCount} batches of up to {BatchSize} for {Project}",
            testClasses.Count, batches.Count, batchSize, projectName);

        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            var batch = batches[batchIndex];
            // vstest filter syntax: combine class-name predicates with `|` (OR), wrap in
            // parentheses so the `&Category!=Flaky` AND binds at the right precedence.
            //   (FullyQualifiedName~ClassA|FullyQualifiedName~ClassB)&Category!=Flaky
            var orClause = string.Join("|", batch.Select(c => $"FullyQualifiedName~{c}"));
            var filter = AppendCategoryFilter($"({orClause})");
            var description = $"{projectName}/batch-{batchIndex + 1}-of-{batches.Count} ({batch.Count} classes)";
            Log.Information("  Running {Description}...", description);

            if (RunTestWithRetry(projectPath, filter, description, frameworkOverride: frameworkOverride))
            {
                continue;
            }

            // Batch retry exhausted. Fall back to per-class so we (a) preserve the original
            // per-class isolation+retry behavior exactly when it matters and (b) get an
            // accurate punch-list of which class(es) actually failed (not "batch 4").
            Log.Warning("  Batch {BatchIndex} failed after retries; falling back to per-class isolation for {Count} classes",
                batchIndex + 1, batch.Count);

            foreach (var className in batch)
            {
                var perClassFilter = AppendCategoryFilter($"FullyQualifiedName~{className}");
                var perClassDescription = $"{projectName}/{className}";
                Log.Information("    Running {Description}...", perClassDescription);

                if (!RunTestWithRetry(projectPath, perClassFilter, perClassDescription, frameworkOverride: frameworkOverride))
                {
                    failedTests.Add(perClassDescription);
                }
            }
        }

        return failedTests;
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
            failedTests.AddRange(RunTestsInBatches(projectPath, projectName, testClasses, frameworkOverride));
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
