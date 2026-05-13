using Newtonsoft.Json;
using Wolverine.Configuration;
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Newtonsoft;

/// <summary>
/// Wolverine 6.0 extension methods that wire Newtonsoft.Json into the
/// serialization pipeline. The Newtonsoft surface lived inline on
/// <see cref="WolverineOptions"/> / <see cref="IEndpointConfiguration{T}"/>
/// in Wolverine 5.x; it moved to this separate package in 6.0 so the core
/// <c>WolverineFx</c> NuGet package no longer carries a Newtonsoft.Json
/// dependency. See the Wolverine 6.0 migration guide.
/// </summary>
public static class WolverineNewtonsoftExtensions
{
    /// <summary>
    ///     Use Newtonsoft.Json as the default JSON serialization for the Wolverine
    ///     application, with optional <see cref="JsonSerializerSettings"/> customization.
    ///     Mirrors the Wolverine 5.x <c>WolverineOptions.UseNewtonsoftForSerialization</c>
    ///     instance method.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="configuration">Optional callback to customize the Newtonsoft settings.</param>
    public static void UseNewtonsoftForSerialization(this WolverineOptions options,
        Action<JsonSerializerSettings>? configuration = null)
    {
        var settings = NewtonsoftSerializer.DefaultSettings();
        configuration?.Invoke(settings);

        var serializer = new NewtonsoftSerializer(settings);

        // Always register the new serializer for its content-type. Then —
        // matching the Wolverine 5.x semantics — replace the default only
        // when the current default is also a JSON serializer. If the user
        // has wired a non-JSON serializer (MessagePack, MemoryPack, …) as
        // the default, leave that default in place; this call just adds
        // Newtonsoft as the registered handler for application/json.
        //
        // (WolverineOptions wires SystemTextJsonSerializer in its
        // constructor, so DefaultSerializer is always non-null here.)
        options.AddSerializer(serializer);
        if (options.DefaultSerializer.ContentType == EnvelopeConstants.JsonContentType)
        {
            options.DefaultSerializer = serializer;
        }
    }

    /// <summary>
    ///     Use custom Newtonsoft.Json settings for this listener / subscriber endpoint
    ///     specifically. Mirrors the Wolverine 5.x
    ///     <c>IEndpointConfiguration&lt;T&gt;.CustomNewtonsoftJsonSerialization</c>
    ///     interface method.
    /// </summary>
    /// <typeparam name="T">The fluent-config self type.</typeparam>
    /// <param name="config">The endpoint configuration.</param>
    /// <param name="customSettings">The Newtonsoft.Json settings to apply to this endpoint.</param>
    /// <returns></returns>
    public static T CustomNewtonsoftJsonSerialization<T>(this IEndpointConfiguration<T> config,
        JsonSerializerSettings customSettings)
    {
        // The 5.x impl only set DefaultSerializer on the endpoint, not
        // RegisterSerializer. Going through the existing public
        // DefaultSerializer(IMessageSerializer) hook gives us the more
        // complete behaviour (register + default in one go), which is what
        // a per-endpoint custom serializer really wants.
        return config.DefaultSerializer(new NewtonsoftSerializer(customSettings));
    }

    /// <summary>
    ///     Use Newtonsoft.Json as the JSON serialization inside the
    ///     MassTransit interop wire format. MassTransit's own wire protocol
    ///     uses Newtonsoft.Json, so callers that need bit-for-bit wire
    ///     compatibility with existing MassTransit producers / consumers
    ///     should use this in preference to the System.Text.Json default.
    ///     Mirrors the Wolverine 5.x <c>IMassTransitInterop.UseNewtonsoftForSerialization</c>
    ///     interface method.
    /// </summary>
    /// <param name="interop"></param>
    /// <param name="configuration">Optional callback to customize the Newtonsoft settings.</param>
    public static void UseNewtonsoftForSerialization(this IMassTransitInterop interop,
        Action<JsonSerializerSettings>? configuration = null)
    {
        // The only implementation of IMassTransitInterop in this assembly
        // (and the wider Wolverine ecosystem) is MassTransitJsonSerializer.
        // Casting to the concrete type lets us reach the internal
        // ApplyInnerSerializer hook without widening the public interface.
        if (interop is not MassTransitJsonSerializer concrete)
        {
            throw new NotSupportedException(
                $"Newtonsoft Wolverine MassTransit interop is only supported when {nameof(IMassTransitInterop)} " +
                $"is an instance of {nameof(MassTransitJsonSerializer)}. Got: {interop.GetType().FullName}.");
        }

        var settings = NewtonsoftSerializer.DefaultSettings();
        configuration?.Invoke(settings);

        concrete.ApplyInnerSerializer(new NewtonsoftSerializer(settings));
    }
}
