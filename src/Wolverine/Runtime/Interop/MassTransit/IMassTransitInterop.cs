using System.Text.Json;

namespace Wolverine.Runtime.Interop.MassTransit;

public interface IMassTransitInterop
{
    /// <summary>
    ///     Use System.Text.Json as the default JSON serialization with optional configuration
    /// </summary>
    /// <param name="configuration"></param>
    void UseSystemTextJsonForSerialization(Action<JsonSerializerOptions>? configuration = null);

    /// <summary>
    ///     Derive the Wolverine <see cref="Envelope.TenantId" /> for incoming MassTransit messages of
    ///     type <typeparamref name="T" /> from the message itself or its MassTransit metadata. The
    ///     supplied lambda receives the strongly-typed MassTransit envelope and returns the tenant id
    ///     (or <c>null</c> / empty to leave the tenant id untouched). This only affects the inbound
    ///     (deserialization) path. Registering multiple message types is supported — each registration
    ///     applies only to its own <typeparamref name="T" />. Registering the same type more than once
    ///     replaces the previous mapping for that type.
    /// </summary>
    /// <param name="tenantIdSource">
    ///     Maps the incoming MassTransit envelope to a tenant id, e.g.
    ///     <c>env =&gt; env.Message?.TenantId</c> or
    ///     <c>env =&gt; env.Headers.TryGetValue("tenant-id", out var v) ? v?.ToString() : null</c>.
    /// </param>
    /// <typeparam name="T">The Wolverine message type to extract the tenant id from.</typeparam>
    IMassTransitInterop MapTenantIdFrom<T>(Func<MassTransitEnvelope<T>, string?> tenantIdSource) where T : class;

    // Newtonsoft.Json variant moved to WolverineFx.Newtonsoft as the
    // UseNewtonsoftForSerialization(this IMassTransitInterop, ...)
    // extension method. Install WolverineFx.Newtonsoft to opt in.
}
