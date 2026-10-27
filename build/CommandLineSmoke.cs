using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

// GH-5125. command-line.yml's describe-handlers and event-model smoke tests, which used to be inline
// shell in that workflow, so nothing but CI could run them. As a target, the workflow and LocalGate run
// the same checks. Each runs the Quickstart sample's own command line, the way an application would.
partial class Build
{
    AbsolutePath QuickstartSample => RootDirectory / "src" / "Samples" / "Quickstart";

    /// <summary>
    /// <c>wolverine-diagnostics describe-handlers</c> finds a known handler, matches a partial name to
    /// several, and exits non-zero for an unknown type; <c>event-model --json</c> writes the merged Event
    /// Model without starting the host.
    /// </summary>
    Target CICommandLineSmoke => _ => _
        .Description("The describe-handlers and event-model smoke tests from command-line.yml. GH-5125.")
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var framework = string.IsNullOrEmpty(Framework) ? "net9.0" : Framework;

            Log.Information("describe-handlers: a known handler is discovered");
            var known = runQuickstart(framework, "wolverine-diagnostics describe-handlers CreateIssueHandler", expectSuccess: true);
            requireAll(known, "describe-handlers CreateIssueHandler",
                "Handler discovery report: Quickstart.CreateIssueHandler",
                "Successfully found Quickstart.CreateIssueHandler during scanning.",
                "HIT -- Method name is 'Handle'");

            Log.Information("describe-handlers: a partial name matches several types");
            var fuzzy = runQuickstart(framework, "wolverine-diagnostics describe-handlers Create", expectSuccess: true);
            var reports = Regex.Matches(fuzzy, Regex.Escape("Handler discovery report:")).Count;
            if (reports < 2)
                throw new Exception($"describe-handlers Create matched {reports} report(s); expected at least 2.");

            Log.Information("describe-handlers: an unknown type exits non-zero");
            runQuickstart(framework, "wolverine-diagnostics describe-handlers ZzzNoSuchHandler", expectSuccess: false);

            Log.Information("event-model: writes the merged Event Model as JSON without starting the host");
            var output = RootDirectory / "artifacts" / "quickstart-event-model.json";
            output.Parent.CreateDirectory();
            output.DeleteFile();
            runQuickstart(framework, $"event-model --json {output}", expectSuccess: true);

            if (!output.FileExists() || new FileInfo(output).Length == 0)
                throw new Exception($"event-model --json wrote nothing to {output}.");

            // GH-4387: a message handler no longer guesses "Command" -- it cannot tell a Command slice
            // from an Automation slice. The MODEL can, though: IssueCreated only ever arrives because
            // CreateIssueHandler cascaded it, which is an Automation.
            requireAll(File.ReadAllText(output), "the event-model JSON",
                "\"name\": \"Quickstart\"", "\"slices\"", "\"name\": \"CreateIssue\"",
                "\"triggerKind\": \"MessageHandler\"", "\"handlerType\"", "\"pattern\": \"Automation\"", "\"elements\"");

            Log.Information("The describe-handlers and event-model smoke tests passed");
        });

    string runQuickstart(string framework, string arguments, bool expectSuccess)
    {
        var process = ProcessTasks.StartProcess("dotnet",
            $"run --project {QuickstartSample} --framework {framework} -- {arguments:nq}",
            workingDirectory: RootDirectory, logOutput: expectSuccess);
        process.WaitForExit();

        var text = string.Join(Environment.NewLine, process.Output.Select(x => x.Text));
        if (expectSuccess && process.ExitCode != 0)
            throw new Exception($"`{arguments}` exited {process.ExitCode}:{Environment.NewLine}{text}");
        if (!expectSuccess && process.ExitCode == 0)
            throw new Exception($"`{arguments}` was expected to exit non-zero and exited 0.");

        return text;
    }

    static void requireAll(string text, string what, params string[] expected)
    {
        var missing = expected.Where(x => !text.Contains(x, StringComparison.Ordinal)).ToArray();
        if (missing.Length > 0)
            throw new Exception($"{what} is missing: {string.Join(" | ", missing)}");
    }
}
