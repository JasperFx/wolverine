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
    [Description("Path of the JSON file to write the Event Model to")]
    [FlagAlias("json", 'j')]
    public string JsonFlag { get; set; } = "event-model.json";

    [Description("Optional name for the assembled model; defaults to the Wolverine service name")]
    public string? NameFlag { get; set; }
}

/// <summary>
///     <c>dotnet run -- event-model [--json &lt;path&gt;]</c>: write the host's merged Event Model as JSON
///     without a running fleet (GH-3990). The host is built but <b>never started</b>: the handler graph is
///     compiled by resolving the code file collections — the <c>wolverine-diagnostics describe-handlers</c>
///     trick — so no transport is opened, no database is touched, and no runtime compiler is needed.
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

    public override async Task<bool> Execute(EventModelInput input)
    {
        if (input.JsonFlag.IsEmpty())
        {
            Console.WriteLine("No file name supplied.");
            return false;
        }

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

            var path = input.JsonFlag.ToFullPath();
            await using (var stream = new FileStream(path, FileMode.Create))
            {
                await WolverineEventModelExport.WriteAsync(model, stream);
                await stream.FlushAsync();
            }

            Console.WriteLine(
                $"Wrote the Event Model '{model.Name}' ({model.Slices.Count} slices, {model.Aggregates.Count} aggregates) to {path}");

            return true;
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }
}
