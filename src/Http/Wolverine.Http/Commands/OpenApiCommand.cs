using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using JasperFx.CommandLine;
using JasperFx.Core;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Wolverine.Http.Commands;

namespace Wolverine.Http;

public class OpenApiInput : NetCoreInput
{
    [Description(
        "The named OpenAPI document to generate. Defaults to 'v1', which matches the default " +
        "document name registered by AddOpenApi(). Use 'openapi --list' to see the documents this " +
        "application exposes.")]
    [FlagAlias("document", 'd')]
    public string DocumentFlag { get; set; } = "v1";

    [Description(
        "File path to write the generated OpenAPI JSON to. When omitted (or set to '-'), the document " +
        "is written to standard output. Note that application and command-line logging is also written " +
        "to the console, so pass --output to capture a clean JSON file.")]
    [FlagAlias("output", 'o')]
    public string OutputFlag { get; set; } = string.Empty;

    [Description("List the OpenAPI document names exposed by this application and exit without generating anything.")]
    [FlagAlias("list", 'l')]
    public bool ListFlag { get; set; }

    [Description(
        "Optional fuzzy filter on the HTTP route. When set, only paths whose route template contains " +
        "this value (case-insensitive) are written, along with the schema components they reference. " +
        "Handy for inspecting the OpenAPI metadata of a single endpoint when troubleshooting. " +
        "Example: --route /todoitems")]
    [FlagAlias("route", 'r')]
    public string RouteFlag { get; set; } = string.Empty;
}

/// <summary>
///     Generates the OpenAPI (Swagger) description document for a Wolverine.HTTP application
///     <i>without</i> performing a full <see cref="Microsoft.Extensions.Hosting.IHost" /> startup.
///     Microsoft's <c>Microsoft.Extensions.ApiDescription.Server</c> tooling (the
///     <c>GetDocument.Insider</c> process used by <c>dotnet build</c> / NSwag) starts the host,
///     which spins up Wolverine's hosted service and attempts to connect to the configured
///     database and/or message broker before a single line of OpenAPI JSON is produced. That makes
///     build-time / CI document generation fragile for any system backed by durable message storage.
///
///     This command instead reuses the host the application already built (Wolverine added,
///     <c>MapWolverineEndpoints()</c> already invoked) and asks the registered OpenAPI document
///     provider — the same <c>IDocumentProvider</c> service <c>GetDocument.Insider</c> resolves — to
///     serialize the document straight from endpoint metadata. The host is never started, so no
///     database or broker connection is ever attempted.
/// </summary>
[Description(
    "Generate the OpenAPI document for this Wolverine.HTTP application without starting the host, " +
    "so no database or message broker connectivity is required",
    Name = "openapi")]
public class OpenApiCommand : JasperFxAsyncCommand<OpenApiInput>
{
    public override async Task<bool> Execute(OpenApiInput input)
    {
        // BuildHost() returns the host the application already configured during bootstrapping.
        // Crucially, MapWolverineEndpoints() runs *before* RunJasperFxCommands(args), so the
        // Wolverine HTTP endpoints are already registered in the EndpointDataSource by the time
        // this command executes. We deliberately do NOT call host.StartAsync(): starting the host
        // is exactly what triggers the database / broker connectivity this command is meant to avoid.
        using var host = input.BuildHost();

        var documentProvider = PrepareDocumentProvider(host);
        if (documentProvider == null)
        {
            AnsiConsole.MarkupLine(
                "[red]No OpenAPI document provider is registered for this application.[/]");
            AnsiConsole.MarkupLine(
                "Add the [bold]Microsoft.AspNetCore.OpenApi[/] package and call " +
                "[bold]builder.Services.AddOpenApi()[/] in your application bootstrapping to enable OpenAPI generation.");
            return false;
        }

        var documentNames = documentProvider.GetDocumentNames();

        if (input.ListFlag)
        {
            WriteDocumentNames(documentNames);
            return true;
        }

        if (documentNames.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[red]The registered OpenAPI document provider did not expose any documents.[/] " +
                "Verify that [bold]AddOpenApi()[/] was called during application bootstrapping.");
            return false;
        }

        if (!documentNames.Contains(input.DocumentFlag, StringComparer.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine(
                $"[red]No OpenAPI document named '[bold]{Markup.Escape(input.DocumentFlag)}[/]' was found.[/]");
            WriteDocumentNames(documentNames);
            return false;
        }

        // A file is written only when an explicit path is given. Otherwise (no --output, or the "-"
        // convention) the JSON goes to standard output.
        var writeToFile = input.OutputFlag.IsNotEmpty() && input.OutputFlag != "-";

        if (input.RouteFlag.IsEmpty())
        {
            // Stream the full document straight from the provider.
            if (writeToFile)
            {
                var path = await WriteFileAsync(input.OutputFlag, w => documentProvider.GenerateAsync(input.DocumentFlag, w));
                AnsiConsole.MarkupLine(
                    $"[green]Wrote OpenAPI document '[bold]{Markup.Escape(input.DocumentFlag)}[/]' to [bold]{Markup.Escape(path)}[/][/]");
            }
            else
            {
                await documentProvider.GenerateAsync(input.DocumentFlag, Console.Out);
                await Console.Out.FlushAsync();
            }

            return true;
        }

        // --route: generate the full document, then keep only the matching routes (plus the schema
        // components they reference).
        var buffer = new StringWriter();
        await documentProvider.GenerateAsync(input.DocumentFlag, buffer);
        var fullDocument = buffer.ToString();

        var filtered = OpenApiRouteFilter.Filter(fullDocument, input.RouteFlag, out var matchedPaths);
        if (matchedPaths.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[red]No HTTP routes matched '[bold]{Markup.Escape(input.RouteFlag)}[/]'.[/]");
            WriteAvailableRoutes(OpenApiRouteFilter.ListPaths(fullDocument));
            return false;
        }

        if (writeToFile)
        {
            var path = await WriteFileAsync(input.OutputFlag, w => w.WriteAsync(filtered));
            AnsiConsole.MarkupLine(
                $"[green]Wrote {matchedPaths.Count} matching route(s) from document '[bold]{Markup.Escape(input.DocumentFlag)}[/]' to [bold]{Markup.Escape(path)}[/]:[/]");
            foreach (var matchedPath in matchedPaths)
            {
                AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(matchedPath)}[/]");
            }
        }
        else
        {
            await Console.Out.WriteAsync(filtered);
            await Console.Out.FlushAsync();
        }

        return true;
    }

    /// <summary>
    ///     Make the application's mapped endpoints visible to the OpenAPI document service and
    ///     resolve the registered provider — without starting the host. Shared by the command and
    ///     by tests so both exercise the identical no-startup generation path. Returns null when no
    ///     OpenAPI document provider is registered (i.e. AddOpenApi() was never called).
    /// </summary>
    internal static OpenApiDocumentProvider? PrepareDocumentProvider(IHost host)
    {
        // The application's endpoint data sources (e.g. the Wolverine HttpGraph added by
        // MapWolverineEndpoints) are normally merged into the global EndpointDataSource by the
        // routing middleware's UseEndpoints step, which only runs once the host starts. We replicate
        // just that merge here so generation sees every endpoint while skipping host startup entirely.
        ComposeEndpointDataSources(host);

        return OpenApiDocumentProvider.Resolve(host.Services);
    }

    private static async Task<string> WriteFileAsync(string outputPath, Func<TextWriter, Task> writeBody)
    {
        var fullPath = Path.GetFullPath(outputPath);

        var directory = Path.GetDirectoryName(fullPath);
        if (directory.IsNotEmpty() && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // UTF-8 without a BOM, matching what GetDocument.Insider writes for OpenAPI documents.
        await using (var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            await writeBody(writer);
            await writer.FlushAsync();
        }

        return fullPath;
    }

    private static void WriteAvailableRoutes(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine("[grey]Available routes:[/]");
        foreach (var path in paths)
        {
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(path)}[/]");
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Dev/build-time 'openapi' CLI command. RouteOptions.EndpointDataSources is an internal collection populated identically by ASP.NET Core's own UseEndpoints; reflecting it here only runs interactively, never on an AOT-published hot path.")]
    private static void ComposeEndpointDataSources(IHost host)
    {
        if (host is not IEndpointRouteBuilder routeBuilder || routeBuilder.DataSources.Count == 0)
        {
            return;
        }

        var routeOptions = host.Services.GetService<IOptions<RouteOptions>>();
        if (routeOptions?.Value == null)
        {
            return;
        }

        var property = typeof(RouteOptions).GetProperty("EndpointDataSources",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (property?.GetValue(routeOptions.Value) is not ICollection<EndpointDataSource> sources)
        {
            return;
        }

        // Mirror EndpointRoutingApplicationBuilderExtensions.UseEndpoints: add every data source the
        // application mapped, de-duplicating so a later real start (if any) stays correct.
        foreach (var dataSource in routeBuilder.DataSources)
        {
            if (!sources.Contains(dataSource))
            {
                sources.Add(dataSource);
            }
        }
    }

    private static void WriteDocumentNames(IReadOnlyList<string> documentNames)
    {
        if (documentNames.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]This application does not expose any OpenAPI documents.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[grey]Available OpenAPI documents:[/]");
        foreach (var name in documentNames)
        {
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(name)}[/]");
        }
    }
}
