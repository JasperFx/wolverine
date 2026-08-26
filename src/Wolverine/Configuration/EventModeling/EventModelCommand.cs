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
    ///     Walk every registered <see cref="IEventModelDefinitionSource" /> through
    ///     <see cref="EventModelDiscovery" /> and fold the result into one model named for the service
    ///     (or <paramref name="modelName" />).
    /// </summary>
    public static async Task<EventModelDescriptor> AssembleAsync(IServiceProvider services, string? modelName = null,
        CancellationToken token = default)
    {
        var assembled = await EventModelDiscovery.AssembleAsync(services, token).ConfigureAwait(false);
        var name = modelName ?? services.GetService<WolverineOptions>()?.ServiceName ?? "Wolverine";
        return EventModelDescriptor.Merge(name, assembled);
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

            var model = await WolverineEventModelExport.AssembleAsync(host.Services, input.NameFlag);
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
