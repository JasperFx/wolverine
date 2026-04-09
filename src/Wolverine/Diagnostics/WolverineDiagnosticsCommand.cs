using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.CommandLine;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Diagnostics;

public class WolverineDiagnosticsInput : NetCoreInput
{
    [Description("Diagnostics sub-command to execute. Valid values: codegen-preview")]
    public string Action { get; set; } = "codegen-preview";

    [FlagAlias("handler", 'h')]
    [Description(
        "For codegen-preview: preview generated code for a specific message handler. " +
        "Accepts a fully-qualified message type name (e.g. MyApp.Orders.CreateOrder), " +
        "a short class name (e.g. CreateOrder), or a handler class name (e.g. CreateOrderHandler).")]
    public string HandlerFlag { get; set; } = string.Empty;

    [FlagAlias("route", 'r')]
    [Description(
        "For codegen-preview: preview generated code for a specific HTTP endpoint. " +
        "Format: 'METHOD /path' (e.g. 'POST /api/orders' or 'GET /api/orders/{id}').")]
    public string RouteFlag { get; set; } = string.Empty;
}

/// <summary>
///     Parent command for Wolverine diagnostics sub-commands. Currently supports:
///     <list type="bullet">
///         <item>
///             <c>codegen-preview --handler &lt;type&gt;</c> — show generated handler adapter code
///         </item>
///         <item>
///             <c>codegen-preview --route "METHOD /path"</c> — show generated HTTP endpoint code
///         </item>
///     </list>
/// </summary>
[Description("Wolverine diagnostics tools for inspecting generated code and runtime behavior",
    Name = "wolverine-diagnostics")]
public class WolverineDiagnosticsCommand : JasperFxAsyncCommand<WolverineDiagnosticsInput>
{
    public WolverineDiagnosticsCommand()
    {
        Usage("Run a diagnostics sub-command (e.g. codegen-preview)").Arguments(x => x.Action)
            .ValidFlags(x => x.HandlerFlag, x => x.RouteFlag);
    }

    public override async Task<bool> Execute(WolverineDiagnosticsInput input)
    {
        switch (input.Action.ToLowerInvariant())
        {
            case "codegen-preview":
                return await RunCodegenPreviewAsync(input);

            default:
                AnsiConsole.MarkupLine(
                    $"[red]Unknown sub-command '{input.Action}'. Valid sub-commands: codegen-preview[/]");
                return false;
        }
    }

    private static async Task<bool> RunCodegenPreviewAsync(WolverineDiagnosticsInput input)
    {
        if (input.HandlerFlag.IsEmpty() && input.RouteFlag.IsEmpty())
        {
            AnsiConsole.MarkupLine(
                "[red]codegen-preview requires either --handler <type-name> or --route \"METHOD /path\".[/]");
            return false;
        }

        // Set codegen mode BEFORE building the host so Wolverine applies lightweight startup
        // (no database connections, no transport connections).
        DynamicCodeBuilder.WithinCodegenCommand = true;

        try
        {
            using var host = input.BuildHost();

            // Starting the host with WithinCodegenCommand=true applies lightweight mode
            // automatically (stubs transports, disables durability). This is necessary to
            // ensure HandlerGraph.Compile() runs and HTTP chains are registered.
            await host.StartAsync();

            var services = host.Services;
            var serviceVariableSource = services.GetService<IServiceVariableSource>();

            if (!input.HandlerFlag.IsEmpty())
            {
                return PreviewHandlerCode(input.HandlerFlag, services, serviceVariableSource);
            }

            return PreviewRouteCode(input.RouteFlag, services, serviceVariableSource);
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    private static bool PreviewHandlerCode(
        string handlerSearch,
        IServiceProvider services,
        IServiceVariableSource? serviceVariableSource)
    {
        var handlerGraph = services.GetRequiredService<HandlerGraph>();
        var chains = handlerGraph.AllChains().ToArray();

        var chain = FindHandlerChain(handlerSearch, chains);

        if (chain == null)
        {
            AnsiConsole.MarkupLine($"[red]No handler found matching '[bold]{Markup.Escape(handlerSearch)}[/]'.[/]");
            AnsiConsole.MarkupLine("[grey]Available message handlers:[/]");
            foreach (var c in chains.Take(30))
            {
                AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(c.MessageType.FullName ?? c.MessageType.Name)}[/]");
            }

            if (chains.Length > 30)
            {
                AnsiConsole.MarkupLine($"  [grey]... and {chains.Length - 30} more[/]");
            }

            return false;
        }

        var code = GenerateSingleFileCode(handlerGraph, chain, serviceVariableSource);
        PrintCodegenResult(
            $"Handler chain for message: {chain.MessageType.FullName}",
            chain.Description,
            code);
        return true;
    }

    internal static HandlerChain? FindHandlerChain(string search, HandlerChain[] chains)
    {
        // 1. Exact full name match on the message type
        var chain = chains.FirstOrDefault(c =>
            string.Equals(c.MessageType.FullName, search, StringComparison.OrdinalIgnoreCase));

        if (chain != null) return chain;

        // 2. Short name match on message type
        chain = chains.FirstOrDefault(c =>
            string.Equals(c.MessageType.Name, search, StringComparison.OrdinalIgnoreCase));

        if (chain != null) return chain;

        // 3. Handler class name match (e.g. "CreateOrderHandler")
        chain = chains.FirstOrDefault(c =>
            c.Handlers.Any(h =>
                string.Equals(h.HandlerType.Name, search, StringComparison.OrdinalIgnoreCase)));

        if (chain != null) return chain;

        // 4. Fuzzy contains match — message type full name or handler class name
        var matches = chains.Where(c =>
            (c.MessageType.FullName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            c.MessageType.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            c.Handlers.Any(h => h.HandlerType.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
        ).ToArray();

        if (matches.Length == 1) return matches[0];

        if (matches.Length > 1)
        {
            AnsiConsole.MarkupLine($"[yellow]Multiple handlers match '[bold]{Markup.Escape(search)}[/]'. Please be more specific:[/]");
            foreach (var m in matches)
            {
                AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(m.MessageType.FullName ?? m.MessageType.Name)}[/]");
            }
        }

        return null;
    }

    private static bool PreviewRouteCode(
        string routeInput,
        IServiceProvider services,
        IServiceVariableSource? serviceVariableSource)
    {
        // Compute the expected file name the HttpChain would generate for this route.
        // This mirrors the logic in HttpChain.determineFileName() so we can match without
        // taking a direct compile-time dependency on Wolverine.Http from core Wolverine.
        var expectedFileName = RouteInputToFileName(routeInput);

        // Search all registered ICodeFileCollection instances (includes HandlerGraph and
        // any supplemental collections such as HttpGraph added by Wolverine.Http).
        var allCollections = services.GetServices<ICodeFileCollection>().ToArray();

        ICodeFileCollection? foundCollection = null;
        ICodeFile? foundFile = null;

        foreach (var collection in allCollections)
        {
            foreach (var file in collection.BuildFiles())
            {
                var fileName = file.FileName.Replace(" ", "_");
                if (string.Equals(fileName, expectedFileName, StringComparison.OrdinalIgnoreCase))
                {
                    foundCollection = collection;
                    foundFile = file;
                    break;
                }
            }

            if (foundFile != null) break;
        }

        if (foundFile == null)
        {
            AnsiConsole.MarkupLine(
                $"[red]No HTTP endpoint found matching '[bold]{Markup.Escape(routeInput)}[/]' " +
                $"(expected file name: [bold]{Markup.Escape(expectedFileName)}[/]).[/]");
            AnsiConsole.MarkupLine("[grey]Available HTTP endpoints (file names):[/]");

            foreach (var collection in allCollections)
            {
                foreach (var f in collection.BuildFiles())
                {
                    // Heuristic: HTTP chain file names start with an HTTP method prefix
                    var fn = f.FileName;
                    if (fn.StartsWith("GET_", StringComparison.OrdinalIgnoreCase) ||
                        fn.StartsWith("POST_", StringComparison.OrdinalIgnoreCase) ||
                        fn.StartsWith("PUT_", StringComparison.OrdinalIgnoreCase) ||
                        fn.StartsWith("DELETE_", StringComparison.OrdinalIgnoreCase) ||
                        fn.StartsWith("PATCH_", StringComparison.OrdinalIgnoreCase))
                    {
                        AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(fn)}[/]");
                    }
                }
            }

            return false;
        }

        var code = GenerateSingleFileCode(foundCollection!, foundFile, serviceVariableSource);
        PrintCodegenResult($"HTTP endpoint: {routeInput}", foundFile.FileName, code);
        return true;
    }

    /// <summary>
    ///     Converts a route string like "POST /api/orders/{id}" to the file name that HttpChain
    ///     would generate (e.g. "POST_api_orders_id"). Mirrors HttpChain.determineFileName().
    /// </summary>
    internal static string RouteInputToFileName(string routeInput)
    {
        var trimmed = routeInput.Trim();
        var spaceIndex = trimmed.IndexOf(' ');

        string method;
        string path;

        if (spaceIndex > 0)
        {
            method = trimmed[..spaceIndex].ToUpperInvariant();
            path = trimmed[(spaceIndex + 1)..];
        }
        else
        {
            method = string.Empty;
            path = trimmed;
        }

        // Mirror HttpChain path processing: strip route constraint suffixes, braces, wildcards, dots
        var pathParts = path
            .Replace("{", "")
            .Replace("}", "")
            .Replace("*", "")
            .Replace("?", "")
            .Replace(".", "_")
            .Split('/')
            .Select(segment => segment.Split(':').First());

        var segments = (method.Length > 0 ? new[] { method } : Array.Empty<string>())
            .Concat(pathParts);

        return string.Join("_", segments).Replace('-', '_').Replace("__", "_").Trim('_');
    }

    private static string GenerateSingleFileCode(
        ICodeFileCollection collection,
        ICodeFile file,
        IServiceVariableSource? serviceVariableSource)
    {
        var generatedAssembly = collection.StartAssembly(collection.Rules);
        file.AssembleTypes(generatedAssembly);

        // Pass the service variable source only when the collection requires IoC resolution
        var svs = collection is ICodeFileCollectionWithServices ? serviceVariableSource : null;
        return generatedAssembly.GenerateCode(svs);
    }

    private static void PrintCodegenResult(string heading, string description, string code)
    {
        AnsiConsole.MarkupLine($"[bold green]{Markup.Escape(heading)}[/]");
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(description)}[/]");
        AnsiConsole.WriteLine();
        // Print the raw code without markup so it is copy-pasteable
        Console.WriteLine(code);
    }
}
