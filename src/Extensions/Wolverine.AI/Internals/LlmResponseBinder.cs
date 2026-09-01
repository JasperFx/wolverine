using System.Text.Json;
using ImTools;
using Microsoft.Extensions.AI;

namespace Wolverine.AI.Internals;

/// <summary>
/// Everything a callout does with its response <see cref="Type" />: find it from the identifier the
/// message carries, build the JSON schema handed to the model, and read the model's answer back.
/// </summary>
/// <remarks>
/// Split out of <see cref="LlmCalloutExecutor" /> so that the three trim-sensitive operations sit in one
/// place with no <c>IWolverineRuntime</c> dependency — which is what lets Wolverine.AI.AotSmoke exercise
/// the real production code under trim analysis rather than a reimplementation of it. The executor keeps
/// only the fallbacks that need the runtime, and those are the ones a trimmed application must not
/// depend on. See GH-4230.
/// </remarks>
public sealed class LlmResponseBinder
{
    private readonly LlmCalloutOptions _options;

    // Schema construction walks the response type's properties, which is meaningfully more expensive
    // than the rest of the per callout work. ImHashMap per the repo's hot path convention: lock free,
    // non-allocating reads, copy on write.
    private ImHashMap<Type, JsonElement> _schemas = ImHashMap<Type, JsonElement>.Empty;

    public LlmResponseBinder(LlmCalloutOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Resolve a response type from the identifier a callout carries, using only the types the
    /// application registered with <see cref="LlmCalloutOptions.RegisterResponseType{TResponse}" />.
    /// The one resolution step a trimmed or AOT application can rely on, because it is a dictionary
    /// lookup over types that are statically rooted by the registration call itself.
    /// </summary>
    public bool TryResolveRegisteredType(string identifier, out Type responseType)
    {
        return _options.RegisteredResponseTypes.TryGetValue(identifier, out responseType!);
    }

    /// <summary>
    /// The JSON schema handed to the model as its response format.
    /// </summary>
    /// <remarks>
    /// <see cref="AIJsonUtilities.CreateJsonSchema" /> carries no trim annotations because it builds the
    /// schema from the <c>JsonTypeInfo</c> the serializer options resolve, so a source generated
    /// <c>JsonSerializerContext</c> makes this reflection free. Note the failure mode when the resolver
    /// cannot see a type: this returns an empty schema rather than throwing, which is why the AOT smoke
    /// test asserts on the rendered schema's contents instead of merely on the call succeeding.
    /// </remarks>
    public JsonElement SchemaFor(Type responseType)
    {
        if (_schemas.TryFind(responseType, out var schema)) return schema;

        schema = AIJsonUtilities.CreateJsonSchema(responseType, serializerOptions: _options.JsonSerializerOptions);
        _schemas = _schemas.AddOrUpdate(responseType, schema);

        return schema;
    }

    /// <summary>
    /// Read the model's answer as <paramref name="responseType" />.
    /// </summary>
    /// <remarks>
    /// Through <c>JsonTypeInfo</c> rather than
    /// <c>JsonSerializer.Deserialize(string, Type, JsonSerializerOptions)</c>, which is the overload
    /// carrying <c>[RequiresUnreferencedCode]</c> and <c>[RequiresDynamicCode]</c>.
    /// </remarks>
    public object? Deserialize(string text, Type responseType)
    {
        return JsonSerializer.Deserialize(text, _options.JsonSerializerOptions.GetTypeInfo(responseType));
    }
}
