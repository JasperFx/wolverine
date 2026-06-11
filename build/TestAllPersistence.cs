using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
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
            RunAllTestsProjects(persistenceDir);
        });

    /// <summary>
    /// Runs a single dotnet test invocation with retry logic.
    /// Returns Passed, Flaky (passed on retry), or Failed.
    /// </summary>
    TestOutcome RunTestWithRetry(string projectPath, 
        string fullTestName, int maxAttempts = 2, string frameworkOverride = null)
    {
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var filter = $"FullyQualifiedName~{EscapeFilterValue(fullTestName)}";
        var description = $"{projectName}/{fullTestName}";
        var framework = frameworkOverride ?? Framework;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (attempt > 1)
                    Log.Warning("  Retry attempt {Attempt}/{MaxAttempts} for {Description}", attempt, maxAttempts, description);

                DotNetTest(c => c
                    .SetProjectFile(projectPath)
                    .SetConfiguration(Configuration)
                    .EnableNoBuild()
                    .EnableNoRestore()
                    .SetFramework(framework)
                    .SetFilter(filter)
                    .AddLoggers($"trx;LogFilePrefix={projectName}-{attempt}.trx"));

                return attempt > 1 ? TestOutcome.Flaky : TestOutcome.Passed;
            }
            catch (Exception ex)
            {
                if (attempt == maxAttempts)
                {
                    Log.Error("  {Description} failed after {Attempts} attempts: {Message}",
                        description, maxAttempts, ex.Message);
                    return TestOutcome.Failed;
                }

                Log.Warning("  {Description} failed on attempt {Attempt}/{MaxAttempts}, will retry: {Message}",
                    description, attempt, maxAttempts, ex.Message);
            }
        }

        return TestOutcome.Failed;
    }

    /// <summary>
    /// Runs all test projects under a directory.
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

    /// <summary>
    /// Runs multiple test project with flaky retry.
    /// </summary>
    void RunTestProjects(string[] projectPaths, string frameworkOverride = null)
    {
        var failedProjects = new List<string>();
        foreach (var projectPath in projectPaths)
        {
            if (!RunWithFlakyRetry(projectPath, frameworkOverride: frameworkOverride))
                failedProjects.Add(projectPath);
        }

        if (failedProjects.Count > 0)
            throw new InvalidOperationException($"Tests failed: {string.Join(", ", failedProjects)}");
    }

    /// <summary>
    /// Runs single test project with flaky retry.
    /// </summary>
    void RunTestProject(string projectPath, string frameworkOverride = null)
    {
        RunTestProjects([projectPath], frameworkOverride: frameworkOverride);
    }

    /// <summary>
    /// Parses a TRX result file and returns the fully-qualified names of failed tests.
    /// </summary>
    static List<string> ParseFailedTestNamesFromTrx(AbsolutePath trxPath)
    {
        var doc = XDocument.Load(trxPath.ToString());
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

        var failedTests = new List<string>();

        // <UnitTestResult outcome="Failed" testName="Namespace.Class.Method" .../>
        foreach (var result in doc.Descendants(ns + "UnitTestResult"))
        {
            var outcome = (string)result.Attribute("outcome");
            if (outcome != "Failed") continue;

            var testName = (string)result.Attribute("testName");
            if (!string.IsNullOrEmpty(testName))
                failedTests.Add(testName);
        }

        return failedTests;
    }

    /// <summary>
    /// Escapes special characters in a test filter value for dotnet test --filter.
    /// Also strips everything after the first '(' since theory parameters are not
    /// needed for substring matching (FullyQualifiedName~ is a prefix/substring match).
    /// </summary>
    static string EscapeFilterValue(string value)
    {
        var paren = value.IndexOf('(');
        if (paren >= 0)
            value = value[..paren];

        return value
            .Replace("&", "%26")
            .Replace("|", "%7C")
            .Replace("=", "%3D")
            .Replace("!", "%21")
            .Replace("~", "%7E");
    }

    /// <summary>
    /// Runs all tests in a project at once, then retries individual failures.
    /// Uses TRX output to discover which tests failed on the first pass.
    /// Flaky tests (pass on retry) are logged separately from hard failures.
    /// Returns true only if all tests pass (possibly after retries).
    /// </summary>
    bool RunWithFlakyRetry(string projectPath, int maxAttempts = 2, string frameworkOverride = null)
    {
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var framework = frameworkOverride ?? Framework;

        Log.Information("=== {Project}: Running all tests ===", projectName);
        try
        {
            DotNetTest(c => c
                .SetProjectFile(projectPath)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetFramework(framework)
                .SetFilter("Category!=Flaky")
                .AddLoggers($"trx;LogFilePrefix={projectName}"));

            Log.Information("=== {Project}: All tests passed ===", projectName);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "=== {Project} First round failed ===", projectName);
        }

        // Parse TRX for failed test names
        var projectDir = (AbsolutePath)Path.GetDirectoryName(projectPath);
        var trxDir = projectDir / "TestResults";
        var trxFiles = trxDir.GlobFiles($"{projectName}*.trx")
            .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
            .ToList();
        if (trxFiles.Count == 0)
        {
            Log.Error("No TRX file found in {ResultsDir}. Can't retry individual tests.", trxDir);
            return false;
        }

        var failedTests = ParseFailedTestNamesFromTrx(trxFiles[0]);

        if (failedTests.Count == 0)
        {
            Log.Warning("=== {Project}: Build failed and no test failures found in TRX. ===", projectName);
            return false;
        }

        Log.Warning("=== {Project}: Second round. Retrying {Count} test(s) ===", projectName, failedTests.Count);

        // Retry each failed test individually
        var result = new RunResult();
        foreach (var fullTestName in failedTests)
        {
            var outcome = RunTestWithRetry(
                projectPath,
                fullTestName,
                maxAttempts,
                framework);

            if (outcome == TestOutcome.Flaky)
                result.FlakyTests.Add(fullTestName);
            else if (outcome == TestOutcome.Failed)
                result.FailedTests.Add(fullTestName);
        }

        result.Print(projectName);

        if (result.FailedTests.Count > 0)
            return false;

        Log.Information("=== {Project}: All tests passed (with flaky retries) ===", projectName);
        return true;
    }

    /// <summary>
    /// Result of a test run: passed first try, passed on retry (flaky), or failed.
    /// </summary>
    enum TestOutcome { Passed, Flaky, Failed }

    class RunResult
    {
        public List<string> FailedTests { get; private set; } = [];
        public List<string> FlakyTests { get; private set; } = [];

        public void Print(string projectName)
        {
            if (FlakyTests.Count != 0)
            {
                var tests = string.Join("\n  ", FlakyTests.Select(t => $"[FLAKY] {t}"));
                Log.Warning("=== {Project} Flaky tests ===\n{Tests}",
                    projectName, tests);
            }

            if (FailedTests.Count != 0)
            {
                var tests = string.Join("\n  ", FailedTests.Select(t => $"[FAILED] {t}"));
                
                Log.Error("=== {Project} Consistently failing tests ===\n{Tests}",
                    projectName, tests);
            }
        }
    }
}