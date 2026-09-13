using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.CodeGeneration;
using JasperFx.CommandLine;
using JasperFx.Core;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Configuration.EventModeling;

/// <summary>
///     Assembles the full Event Model of a host — Wolverine's derived chain roles, Wolverine.HTTP's, and
///     any jasperfx#687 overlay the application registered — and writes it as JSON (GH-3990). The JSON is
///     the wire descriptor exactly as CritterWatch serialises it (camelCase, enums as strings), so the
///     file round-trips through <see cref="EventModelDescriptor" /> and renders in the shared Event
///     Modeling component with the same output CritterWatch shows for the same host.
/// </summary>
public static class WolverineEventModelExport
{
    /// <summary>
    ///     The serializer settings the export writes with — the same shape CritterWatch puts on the wire.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = buildSerializerOptions();

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "CLI / diagnostic path, not dispatch; the non-generic JsonStringEnumConverter is what CritterWatch's wire format uses, and the descriptor tree is reflection-serialised like ServiceCapabilities.")]
    private static JsonSerializerOptions buildSerializerOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    ///     <see cref="AssembleSetAsync" />, folded into a single descriptor for a caller whose wire cannot
    ///     carry more than one model.
    /// </summary>
    /// <remarks>
    ///     <b>Lossy when the host hosts several models — and now says so.</b> The fold is
    ///     <see cref="EventModelSetDescriptor.Collapse" />, which appends a <c>ModelCollapse</c> hotspot
    ///     naming every model that went in. GH-4424: this path used to do the same fold implicitly and
    ///     report nothing at all, so a host assembling two models lost one of the names outright and had
    ///     its slices merged into the other, under a label naming the service.
    /// </remarks>
    public static async Task<EventModelDescriptor> AssembleAsync(IServiceProvider services, string? modelName = null,
        CancellationToken token = default)
    {
        var set = await AssembleSetAsync(services, token: token).ConfigureAwait(false);

        // GH-4424: a name SELECTS a model when the host has one by that name — the flag chooses rather
        // than renames. Failing that it names the collapsed model, which is what it always did.
        if (modelName is not null && set.Find(modelName) is { } selected) return selected;

        return set.Collapse(modelName);
    }

    /// <summary>
    ///     Walk every registered <see cref="IEventModelDefinitionSource" /> and return the models this
    ///     service hosts — one per model name — inside the service-scoped
    ///     <see cref="EventModelSetDescriptor" /> envelope. GH-4424.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     Several models in one host is legal and increasingly ordinary: each of the three Critter Stack
    ///     stores can name its own model through <c>StoreOptions.EventModelName</c>, and a modular monolith
    ///     registers an ancillary store per module. <see cref="AssembleAsync" /> folds them for a caller
    ///     that cannot carry more than one; this is the shape that keeps them.
    ///     </para>
    ///     <para>
    ///     GH-4385: the descriptors go through <see cref="EventModelSliceAlignment" /> first, so a declared
    ///     model and the code it describes merge on the handler type they agree about rather than sliding
    ///     past each other on names they were never going to compute the same way.
    ///     </para>
    ///     <para>
    ///     This deliberately does <b>not</b> call <c>EventModelDiscovery.AssembleSetAsync</c>, which has
    ///     exactly this signature and would otherwise be the obvious delegation: it walks
    ///     <c>DiscoverAsync</c> itself, so it never sees the alignment above and would silently drop it.
    ///     Grouping by name is <see cref="EventModelSetDescriptor.For" />, which is the part of it that is
    ///     wanted here.
    ///     </para>
    /// </remarks>
    public static async Task<EventModelSetDescriptor> AssembleSetAsync(IServiceProvider services,
        string? serviceName = null, CancellationToken token = default)
    {
        var discovered = await EventModelDiscovery.DiscoverAsync(services, token).ConfigureAwait(false);
        var aligned = EventModelSliceAlignment.AlignSliceNames(discovered);
        var name = serviceName ?? services.GetService<WolverineOptions>()?.ServiceName ?? "Wolverine";

        return EventModelSetDescriptor.For(name, aligned);
    }

    /// <summary>Serialize a model with <see cref="SerializerOptions" />.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "CLI / diagnostic path, not dispatch; the descriptor tree is bounded and reflection-serialised like ServiceCapabilities.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "CLI / diagnostic path, not dispatch; the descriptor tree is bounded and reflection-serialised like ServiceCapabilities.")]
    public static Task WriteAsync(EventModelDescriptor model, Stream stream, CancellationToken token = default)
        => JsonSerializer.SerializeAsync(stream, model, SerializerOptions, token);

    /// <summary>Serialize a model with <see cref="SerializerOptions" />.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "CLI / diagnostic path, not dispatch; the descriptor tree is bounded and reflection-serialised like ServiceCapabilities.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "CLI / diagnostic path, not dispatch; the descriptor tree is bounded and reflection-serialised like ServiceCapabilities.")]
    public static string ToJson(EventModelDescriptor model) => JsonSerializer.Serialize(model, SerializerOptions);

    /// <summary>Read a model back from the JSON the export wrote.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "CLI / diagnostic path, not dispatch; the descriptor tree is bounded and reflection-serialised like ServiceCapabilities.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "CLI / diagnostic path, not dispatch; the descriptor tree is bounded and reflection-serialised like ServiceCapabilities.")]
    public static EventModelDescriptor? FromJson(string json) => JsonSerializer.Deserialize<EventModelDescriptor>(json, SerializerOptions);
}

public class EventModelInput : NetCoreInput
{
    /// <summary>
    ///     GH-4146: defaults to null rather than to <c>event-model.json</c> so that <c>--url</c> on its own
    ///     publishes without also dropping a file next to the application. With neither flag the command
    ///     still writes <see cref="DefaultJsonFile" />, exactly as it always has.
    /// </summary>
    [Description("Path of the JSON file to write the Event Model to; defaults to event-model.json unless --url is given")]
    [FlagAlias("json", 'j')]
    public string? JsonFlag { get; set; }

    [Description("Optional name for the assembled model; defaults to the Wolverine service name")]
    public string? NameFlag { get; set; }

    /// <summary>
    ///     GH-4146: PUT the assembled descriptor to a monitor instead of (or as well as) writing it to a
    ///     file, so the design-time loop is one command: <c>dotnet watch run -- event-model --url ...</c>.
    /// </summary>
    [Description("URL of a monitor to PUT the assembled Event Model to; composes with --json")]
    [FlagAlias("url", 'u')]
    public string? UrlFlag { get; set; }

    /// <summary>Where the Event Model goes when neither <c>--json</c> nor <c>--url</c> is supplied.</summary>
    public const string DefaultJsonFile = "event-model.json";

    /// <summary>
    ///     The file to write, or null when <c>--url</c> was given without <c>--json</c> and the descriptor
    ///     should only be published.
    /// </summary>
    internal string? ResolveJsonPath()
    {
        if (JsonFlag.IsNotEmpty())
        {
            return JsonFlag;
        }

        return UrlFlag.IsEmpty() ? DefaultJsonFile : null;
    }
}

/// <summary>
///     <c>dotnet run -- event-model [--json &lt;path&gt;] [--url &lt;monitor&gt;]</c>: write the host's merged
///     Event Model as JSON, publish it to a monitor, or both — without a running fleet (GH-3990). The host is
///     built but <b>never started</b>: the handler graph is compiled by resolving the code file collections —
///     the <c>wolverine-diagnostics describe-handlers</c> trick — so no transport is opened, no database is
///     touched, and no runtime compiler is needed.
///
///     <para>GH-4146: with <c>--url</c> the whole design-time loop becomes
///     <c>dotnet watch run -- event-model --url http://localhost:5525</c>. Note that the rebuild has to come
///     from <c>dotnet watch</c> and not from a <c>--watch</c> flag here: this process already has the
///     assembly loaded, so an internal loop would re-serialise the same chains forever and never see an
///     edit. Only a fresh process picks up recompiled handlers.</para>
/// </summary>
[Description("Write the application's Event Model — the roles every handler, HTTP and gRPC chain derives about itself, plus any registered overlay — as JSON, without a running fleet",
    Name = "event-model")]
public class EventModelCommand : JasperFxAsyncCommand<EventModelInput>
{
    public EventModelCommand()
    {
        Usage("Write the Event Model to event-model.json");
        Usage("Write the Event Model to the designated file").Arguments();
    }

    /// <summary>
    ///     GH-4146: how long to wait on the monitor before giving up. The point of <c>--url</c> is a fast
    ///     design-time loop, so a monitor that is not answering has to fail quickly rather than stall
    ///     <c>dotnet watch</c>.
    /// </summary>
    internal static TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public override async Task<bool> Execute(EventModelInput input)
    {
        Uri? monitor = null;
        if (input.UrlFlag.IsNotEmpty())
        {
            if (!Uri.TryCreate(input.UrlFlag, UriKind.Absolute, out monitor) ||
                (monitor.Scheme != Uri.UriSchemeHttp && monitor.Scheme != Uri.UriSchemeHttps))
            {
                Console.WriteLine($"'{input.UrlFlag}' is not a valid absolute http:// or https:// URL.");
                return false;
            }
        }

        var jsonPath = input.ResolveJsonPath();

        // Set BEFORE the host is built, exactly as the codegen and wolverine-diagnostics commands do,
        // so Wolverine bootstraps in lightweight mode — no handler registry consumption, no
        // transport or durability side effects.
        DynamicCodeBuilder.WithinCodegenCommand = true;

        try
        {
            using var host = input.BuildHost();

            // The host is NOT started. Resolving the code file collections compiles the handler graph
            // (the same trick wolverine-diagnostics describe-handlers uses): no transports are opened,
            // no database is touched, and no Roslyn is needed — a TypeLoadMode.Dynamic app without
            // WolverineFx.RuntimeCompilation still exports. Wolverine.HTTP's chains were discovered
            // when the application mapped its endpoints, before this command ran, so they are there too.
            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var set = await WolverineEventModelExport.AssembleSetAsync(host.Services);

            // GH-4424. --name SELECTS which model to export rather than renaming a fold of all of them,
            // and an unnamed export of a host with several no longer picks a survivor in silence.
            EventModelDescriptor model;
            if (input.NameFlag.IsNotEmpty())
            {
                if (set.Find(input.NameFlag!) is not { } selected)
                {
                    var hosted = set.Models.Count == 0
                        ? "no Event Models at all"
                        : string.Join(", ", set.Models.Select(x => $"'{x.Name}'"));
                    Console.WriteLine(
                        $"This application hosts no Event Model named '{input.NameFlag}'. It hosts {hosted}.");
                    return false;
                }

                model = selected;
            }
            else if (set.Sole is { } sole)
            {
                model = sole;
            }
            else
            {
                model = set.Collapse();

                if (set.IsAmbiguous)
                {
                    Console.WriteLine(
                        $"This application hosts {set.Models.Count} Event Models ({string.Join(", ", set.Models.Select(x => $"'{x.Name}'"))}), so they were folded into one named '{model.Name}' with a ModelCollapse hotspot recording the loss. Pass --name to export one of them on its own.");
                }
            }

            var summary =
                $"the Event Model '{model.Name}' ({model.Slices.Count} slices, {model.Aggregates.Count} aggregates)";

            if (jsonPath is not null)
            {
                var path = jsonPath.ToFullPath();
                if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                }

                await using (var stream = new FileStream(path, FileMode.Create))
                {
                    await WolverineEventModelExport.WriteAsync(model, stream);
                    await stream.FlushAsync();
                }

                Console.WriteLine($"Wrote {summary} to {path}");
            }

            if (monitor is not null)
            {
                return await publishAsync(model, monitor, summary);
            }

            return true;
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    /// <summary>
    ///     GH-4146. PUT the descriptor to the monitor as the same JSON the file form writes. Wolverine takes
    ///     no reference on the monitor — this is an HTTP PUT to whatever URL the caller names, the
    ///     wire-not-reference posture CritterWatch already takes — so any endpoint that accepts the
    ///     descriptor works, and nothing here knows what is on the other end.
    ///
    ///     <para>Every failure is reported as a sentence and a non-zero exit rather than a stack trace: this
    ///     runs inside <c>dotnet watch</c>, where a monitor that is simply not running yet is the ordinary
    ///     case and must not look like a crash.</para>
    /// </summary>
    private static async Task<bool> publishAsync(EventModelDescriptor model, Uri monitor, string summary)
    {
        using var client = new HttpClient { Timeout = PublishTimeout };

        try
        {
            var json = WolverineEventModelExport.ToJson(model);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PutAsync(monitor, content);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine(
                    $"The monitor at {monitor} rejected {summary}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
                if (body.IsNotEmpty())
                {
                    Console.WriteLine(body.Trim());
                }

                return false;
            }

            Console.WriteLine($"Published {summary} to {monitor}");
            return true;
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine($"The monitor at {monitor} did not respond within {PublishTimeout.TotalSeconds:0.#} seconds.");
            return false;
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine($"Could not reach the monitor at {monitor}: {e.Message}");
            return false;
        }
    }
}
