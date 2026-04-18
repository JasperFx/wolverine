using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.CommandLine;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;
using Wolverine.Transports.Local;

namespace Wolverine.Diagnostics;

public class WolverineDiagnosticsInput : NetCoreInput
{
    [Description("Diagnostics sub-command to execute. Valid values: codegen-preview, describe-routing")]
    public string Action { get; set; } = "codegen-preview";

    [Description("For describe-routing: the message type name to inspect. " +
                 "Accepts full name, short name, or alias.")]
    public string MessageTypeArg { get; set; } = string.Empty;

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

    [FlagAlias("grpc", 'g')]
    [Description(
        "For codegen-preview: preview generated code for a proto-first gRPC service chain. " +
        "Accepts the proto service name (e.g. 'Greeter'), the stub class name (e.g. 'GreeterGrpcService'), " +
        "or the generated file name (e.g. 'GreeterGrpcHandler').")]
    public string GrpcFlag { get; set; } = string.Empty;

    [FlagAlias("all", 'a')]
    [Description("For describe-routing: show complete routing topology for all known message types.")]
    public bool AllFlag { get; set; }
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
///         <item>
///             <c>codegen-preview --grpc &lt;service&gt;</c> — show generated proto-first gRPC service wrapper code
///         </item>
///         <item>
///             <c>describe-routing &lt;MessageType&gt;</c> — inspect message routing for a specific type
///         </item>
///         <item>
///             <c>describe-routing --all</c> — show complete routing topology
///         </item>
///     </list>
/// </summary>
[Description("Wolverine diagnostics tools for inspecting generated code and runtime behavior",
    Name = "wolverine-diagnostics")]
public class WolverineDiagnosticsCommand : JasperFxAsyncCommand<WolverineDiagnosticsInput>
{
    public WolverineDiagnosticsCommand()
    {
        Usage("Run a diagnostics sub-command (e.g. codegen-preview, describe-routing --all)")
            .Arguments(x => x.Action)
            .ValidFlags(x => x.HandlerFlag, x => x.RouteFlag, x => x.GrpcFlag, x => x.AllFlag);

        Usage("Describe message routing for a specific type")
            .Arguments(x => x.Action, x => x.MessageTypeArg);
    }

    public override async Task<bool> Execute(WolverineDiagnosticsInput input)
    {
        switch (input.Action.ToLowerInvariant())
        {
            case "codegen-preview":
                return await RunCodegenPreviewAsync(input);

            case "describe-routing":
                return await RunDescribeRoutingAsync(input);

            default:
                AnsiConsole.MarkupLine(
                    $"[red]Unknown sub-command '{input.Action}'. Valid sub-commands: codegen-preview, describe-routing[/]");
                return false;
        }
    }

    private static async Task<bool> RunCodegenPreviewAsync(WolverineDiagnosticsInput input)
    {
        if (input.HandlerFlag.IsEmpty() && input.RouteFlag.IsEmpty() && input.GrpcFlag.IsEmpty())
        {
            AnsiConsole.MarkupLine(
                "[red]codegen-preview requires one of --handler <type-name>, --route \"METHOD /path\", or --grpc <service-name>.[/]");
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

            if (!input.GrpcFlag.IsEmpty())
            {
                return PreviewGrpcCode(input.GrpcFlag, services, serviceVariableSource);
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

    private static bool PreviewGrpcCode(
        string grpcSearch,
        IServiceProvider services,
        IServiceVariableSource? serviceVariableSource)
    {
        var expectedFileName = GrpcInputToFileName(grpcSearch);

        // Search all registered ICodeFileCollection instances. GrpcGraph (when Wolverine.Grpc
        // is referenced) registers itself through this seam the same way HttpGraph does.
        var allCollections = services.GetServices<ICodeFileCollection>().ToArray();

        ICodeFileCollection? foundCollection = null;
        ICodeFile? foundFile = null;

        foreach (var collection in allCollections)
        {
            foreach (var file in collection.BuildFiles())
            {
                if (string.Equals(file.FileName, expectedFileName, StringComparison.OrdinalIgnoreCase))
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
                $"[red]No gRPC service chain found matching '[bold]{Markup.Escape(grpcSearch)}[/]' " +
                $"(expected file name: [bold]{Markup.Escape(expectedFileName)}[/]).[/]");
            AnsiConsole.MarkupLine("[grey]Available gRPC service chains (file names):[/]");

            var any = false;
            foreach (var collection in allCollections)
            {
                foreach (var f in collection.BuildFiles())
                {
                    if (f.FileName.EndsWith("GrpcHandler", StringComparison.OrdinalIgnoreCase))
                    {
                        AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(f.FileName)}[/]");
                        any = true;
                    }
                }
            }

            if (!any)
            {
                AnsiConsole.MarkupLine(
                    "  [grey](none — is Wolverine.Grpc referenced and are proto-first stubs discovered?)[/]");
            }

            return false;
        }

        var code = GenerateSingleFileCode(foundCollection!, foundFile, serviceVariableSource);
        PrintCodegenResult($"gRPC service chain: {grpcSearch}", foundFile.FileName, code);
        return true;
    }

    /// <summary>
    ///     Normalizes a user-supplied gRPC search input into the file name a
    ///     <c>GrpcServiceChain</c> would generate. The chain's file name is always
    ///     <c>{ProtoServiceName}GrpcHandler</c>, so:
    ///     <list type="bullet">
    ///         <item>Bare proto service name (e.g. <c>Greeter</c>) — append <c>GrpcHandler</c>.</item>
    ///         <item>Stub class name (e.g. <c>GreeterGrpcService</c>) — swap the <c>Service</c> suffix for <c>Handler</c>.</item>
    ///         <item>Already-normalized file name (e.g. <c>GreeterGrpcHandler</c>) — pass through.</item>
    ///     </list>
    /// </summary>
    internal static string GrpcInputToFileName(string grpcInput)
    {
        var trimmed = grpcInput.Trim();

        if (trimmed.EndsWith("GrpcHandler", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.EndsWith("GrpcService", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[..^"Service".Length] + "Handler";
        }

        return trimmed + "GrpcHandler";
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

    // -------------------------------------------------------------------------
    // describe-routing implementation
    // -------------------------------------------------------------------------

    private static async Task<bool> RunDescribeRoutingAsync(WolverineDiagnosticsInput input)
    {
        if (!input.AllFlag && input.MessageTypeArg.IsEmpty())
        {
            AnsiConsole.MarkupLine(
                "[red]describe-routing requires either a message type argument or the --all flag.[/]");
            AnsiConsole.MarkupLine(
                "[grey]Usage: wolverine-diagnostics describe-routing <MessageType>[/]");
            AnsiConsole.MarkupLine(
                "[grey]       wolverine-diagnostics describe-routing --all[/]");
            return false;
        }

        DynamicCodeBuilder.WithinCodegenCommand = true;

        try
        {
            using var host = input.BuildHost();
            await host.StartAsync();

            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

            WolverineSystemPart.WithinDescription = true;
            try
            {
                if (input.AllFlag)
                {
                    DescribeAllRouting(runtime);
                    return true;
                }

                return DescribeSingleTypeRouting(input.MessageTypeArg, runtime);
            }
            finally
            {
                WolverineSystemPart.WithinDescription = false;
            }
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    private static void DescribeAllRouting(IWolverineRuntime runtime)
    {
        var options = runtime.Options;
        var messageTypes = options.Discovery.FindAllMessages(options.HandlerGraph)
            .Where(t => t.Assembly != typeof(WolverineDiagnosticsCommand).Assembly)
            .OrderBy(t => t.FullName)
            .ToArray();

        // --- Routing conventions ---
        AnsiConsole.MarkupLine("[bold green]Routing Conventions[/]");
        if (options.RoutingConventions.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]  (none registered)[/]");
        }
        else
        {
            foreach (var convention in options.RoutingConventions)
            {
                AnsiConsole.MarkupLine($"  [cyan]{Markup.Escape(convention.GetType().FullNameInCode())}[/]");
            }
        }

        AnsiConsole.WriteLine();

        // --- Complete message routing table ---
        AnsiConsole.MarkupLine("[bold green]Message Routing[/]");
        var routingTable = new Table()
            .AddColumn(".NET Type")
            .AddColumn("Handler?")
            .AddColumn("Destination")
            .AddColumn("Mode")
            .AddColumn("Outbox")
            .AddColumn("Serializer");

        var unrouted = new List<Type>();

        foreach (var messageType in messageTypes)
        {
            var routes = runtime.RoutingFor(messageType).Routes;
            var hasHandler = options.HandlerGraph.CanHandle(messageType);
            var handlerLabel = hasHandler ? "[green]Yes[/]" : "[grey]No[/]";
            var shortName = messageType.FullNameInCode().EscapeMarkup();

            if (!routes.Any())
            {
                unrouted.Add(messageType);
                routingTable.AddRow(shortName, hasHandler ? "Yes" : "No", "[yellow]No routes[/]", "", "", "");
                continue;
            }

            foreach (var route in routes.OfType<MessageRoute>())
            {
                var endpointInfo = FindEndpointByUri(route.Uri, options);
                var mode = endpointInfo?.Mode.ToString() ?? (route.IsLocal ? "Buffered" : "Unknown");
                var isDurable = endpointInfo?.Mode == EndpointMode.Durable;
                var serializer = route.Serializer?.ContentType
                                 ?? endpointInfo?.DefaultSerializer?.ContentType
                                 ?? "application/json";

                routingTable.AddRow(
                    shortName,
                    hasHandler ? "Yes" : "No",
                    route.Uri.ToString().EscapeMarkup(),
                    mode.EscapeMarkup(),
                    isDurable ? "[green]Yes[/]" : "No",
                    serializer.EscapeMarkup());

                shortName = ""; // Only show type name on first row
                handlerLabel = "";
            }

            // Handle non-MessageRoute routes (partitioned, transformed, etc.)
            foreach (var route in routes.Where(r => r is not MessageRoute))
            {
                var descriptor = route.Describe();
                routingTable.AddRow(
                    shortName,
                    hasHandler ? "Yes" : "No",
                    descriptor.Endpoint.ToString().EscapeMarkup(),
                    "",
                    "n/a",
                    descriptor.ContentType.EscapeMarkup());

                shortName = "";
                handlerLabel = "";
            }
        }

        AnsiConsole.Write(routingTable);

        if (unrouted.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold yellow]Unrouted Message Types (no destinations)[/]");
            foreach (var t in unrouted)
            {
                AnsiConsole.MarkupLine($"  [yellow]{t.FullNameInCode().EscapeMarkup()}[/]");
            }
        }

        AnsiConsole.WriteLine();

        // --- Listener topology ---
        AnsiConsole.MarkupLine("[bold green]Listeners[/]");
        var listeners = options.Transports
            .SelectMany(t => t.Endpoints())
            .Where(e => e.IsListener || e is LocalQueue)
            .OrderBy(e => e.Uri.ToString())
            .ToArray();

        if (listeners.Length == 0)
        {
            AnsiConsole.MarkupLine("[grey]  (none)[/]");
        }
        else
        {
            var listenerTable = new Table()
                .AddColumn("URI")
                .AddColumn("Name")
                .AddColumn("Mode")
                .AddColumn("Parallelism");

            foreach (var ep in listeners)
            {
                listenerTable.AddRow(
                    ep.Uri.ToString().EscapeMarkup(),
                    ep.EndpointName.EscapeMarkup(),
                    ep.Mode.ToString(),
                    ep.MaxDegreeOfParallelism.ToString());
            }

            AnsiConsole.Write(listenerTable);
        }

        AnsiConsole.WriteLine();

        // --- Sender topology ---
        AnsiConsole.MarkupLine("[bold green]Senders[/]");
        var senders = options.Transports
            .SelectMany(t => t.Endpoints())
            .Where(e => !e.IsListener && e is not LocalQueue && e.Role != EndpointRole.System)
            .OrderBy(e => e.Uri.ToString())
            .ToArray();

        if (senders.Length == 0)
        {
            AnsiConsole.MarkupLine("[grey]  (none)[/]");
        }
        else
        {
            var senderTable = new Table()
                .AddColumn("URI")
                .AddColumn("Name")
                .AddColumn("Mode")
                .AddColumn("Subscriptions");

            foreach (var ep in senders)
            {
                senderTable.AddRow(
                    ep.Uri.ToString().EscapeMarkup(),
                    ep.EndpointName.EscapeMarkup(),
                    ep.Mode.ToString(),
                    ep.Subscriptions.Count.ToString());
            }

            AnsiConsole.Write(senderTable);
        }
    }

    private static bool DescribeSingleTypeRouting(string search, IWolverineRuntime runtime)
    {
        var options = runtime.Options;

        // Find the message type
        var messageTypes = options.Discovery.FindAllMessages(options.HandlerGraph).ToArray();
        var messageType = FindMessageType(search, messageTypes, options.HandlerGraph);

        if (messageType == null)
        {
            AnsiConsole.MarkupLine(
                $"[red]No message type found matching '[bold]{Markup.Escape(search)}[/]'.[/]");
            AnsiConsole.MarkupLine("[grey]Known message types (first 30):[/]");
            foreach (var t in messageTypes.Take(30))
            {
                AnsiConsole.MarkupLine($"  [grey]{t.FullNameInCode().EscapeMarkup()}[/]");
            }
            if (messageTypes.Length > 30)
            {
                AnsiConsole.MarkupLine($"  [grey]... and {messageTypes.Length - 30} more[/]");
            }
            return false;
        }

        // --- Message info ---
        AnsiConsole.MarkupLine($"[bold green]Message Type: {messageType.FullNameInCode().EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine($"  [grey]Assembly:[/] {messageType.Assembly.GetName().Name?.EscapeMarkup()}");
        AnsiConsole.MarkupLine($"  [grey]Namespace:[/] {(messageType.Namespace ?? "(none)").EscapeMarkup()}");

        // Message-level attributes (ModifyEnvelopeAttribute)
        var envelopeRules = MessageRoute.RulesForMessageType(messageType).ToArray();
        if (envelopeRules.Length > 0)
        {
            AnsiConsole.MarkupLine("  [grey]Message attributes:[/]");
            foreach (var rule in envelopeRules)
            {
                AnsiConsole.MarkupLine($"    [cyan]{rule.GetType().Name.EscapeMarkup()}[/]");
            }
        }

        // LocalQueue attribute
        var localQueueAttr = messageType.GetCustomAttributes(typeof(LocalQueueAttribute), false)
            .OfType<LocalQueueAttribute>().FirstOrDefault();
        if (localQueueAttr != null)
        {
            AnsiConsole.MarkupLine(
                $"  [cyan][LocalQueue(\"{localQueueAttr.QueueName.EscapeMarkup()}\")][/]");
        }

        AnsiConsole.WriteLine();

        // --- Handler chain ---
        var chain = options.HandlerGraph.ChainFor(messageType);
        if (chain != null)
        {
            AnsiConsole.MarkupLine("[bold]Local Handler:[/]");
            foreach (var handler in chain.Handlers)
            {
                AnsiConsole.MarkupLine(
                    $"  [cyan]{handler.HandlerType.FullNameInCode().EscapeMarkup()}.{handler.Method.Name}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]Local Handler: (none)[/]");
        }

        AnsiConsole.WriteLine();

        // --- Routes ---
        var routes = runtime.RoutingFor(messageType).Routes;

        if (!routes.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No routes found for this message type.[/]");
            AnsiConsole.MarkupLine(
                "[grey]The message will not be delivered anywhere when published.[/]");
            return true;
        }

        AnsiConsole.MarkupLine("[bold]Routes:[/]");
        var table = new Table()
            .AddColumn("Destination")
            .AddColumn("Type")
            .AddColumn("Mode")
            .AddColumn("Outbox")
            .AddColumn("Serializer")
            .AddColumn("Resolution");

        foreach (var route in routes.OfType<MessageRoute>())
        {
            var endpointInfo = FindEndpointByUri(route.Uri, options);
            var type = route.IsLocal ? "[green]Local[/]" : "External";
            var mode = endpointInfo?.Mode.ToString() ?? (route.IsLocal ? "Buffered" : "Unknown");
            var isDurable = endpointInfo?.Mode == EndpointMode.Durable;
            var serializer = route.Serializer?.ContentType
                             ?? endpointInfo?.DefaultSerializer?.ContentType
                             ?? "application/json";
            var resolution = DetermineResolutionMethod(messageType, route.Uri, route.IsLocal, options);

            table.AddRow(
                route.Uri.ToString().EscapeMarkup(),
                type,
                mode.EscapeMarkup(),
                isDurable ? "[green]Yes (outbox)[/]" : "No",
                serializer.EscapeMarkup(),
                resolution.EscapeMarkup());
        }

        foreach (var route in routes.Where(r => r is not MessageRoute))
        {
            var descriptor = route.Describe();
            table.AddRow(
                descriptor.Endpoint.ToString().EscapeMarkup(),
                "Partitioned",
                "",
                "n/a",
                descriptor.ContentType.EscapeMarkup(),
                "Partitioned topology");
        }

        AnsiConsole.Write(table);
        return true;
    }

    internal static Type? FindMessageType(string search, IReadOnlyList<Type> messageTypes, HandlerGraph handlerGraph)
    {
        // 1. Exact full name match
        var match = messageTypes.FirstOrDefault(t =>
            string.Equals(t.FullName, search, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        // 2. Short name match
        match = messageTypes.FirstOrDefault(t =>
            string.Equals(t.Name, search, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        // 3. Message type alias (as registered in HandlerGraph)
        if (handlerGraph.TryFindMessageType(search, out var aliasMatch)) return aliasMatch;

        // 4. Fuzzy contains match on full name or short name
        var fuzzy = messageTypes.Where(t =>
            (t.FullName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            t.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (fuzzy.Length == 1) return fuzzy[0];

        if (fuzzy.Length > 1)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Multiple message types match '[bold]{Markup.Escape(search)}[/]'. Please be more specific:[/]");
            foreach (var t in fuzzy)
            {
                AnsiConsole.MarkupLine($"  [yellow]{t.FullNameInCode().EscapeMarkup()}[/]");
            }
        }

        return null;
    }

    private static Endpoint? FindEndpointByUri(Uri uri, WolverineOptions options)
    {
        return options.Transports.AllEndpoints().FirstOrDefault(e => e.Uri == uri);
    }

    private static string DetermineResolutionMethod(Type messageType, Uri routeUri, bool isLocal,
        WolverineOptions options)
    {
        if (isLocal)
        {
            // Check for [LocalQueue] attribute on the message type
            var hasAttr = messageType.GetCustomAttributes(typeof(LocalQueueAttribute), false).Any();
            if (hasAttr) return "LocalQueue attribute";

            // Check for explicit local queue assignment
            if (options.LocalRouting.Assignments.TryGetValue(messageType, out _))
            {
                return "Explicit local routing";
            }

            return "Local handler convention";
        }

        // External route — check if the endpoint has an explicit subscription for this type
        var endpoint = FindEndpointByUri(routeUri, options);
        if (endpoint != null && endpoint.ShouldSendMessage(messageType))
        {
            return "Explicit publish rule";
        }

        return "Transport routing convention";
    }
}
