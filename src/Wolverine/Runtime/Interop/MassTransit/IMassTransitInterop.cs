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

    /// <summary>
    ///     Read MassTransit's <c>MessageData&lt;T&gt;</c> claim-check references on incoming messages,
    ///     hydrating each <c>[Blob]</c>-marked property from <paramref name="store"/>. This lets a Wolverine
    ///     service consume large-message envelopes produced by a MassTransit service that shares the same
    ///     blob/object store. Read side only — Wolverine does not produce MassTransit-compatible references.
    ///     See GH-3510.
    /// </summary>
    /// <remarks>
    ///     MassTransit's address carries the object key but <b>not</b> the bucket or container, which come
    ///     from the MassTransit repository's own configuration. <paramref name="store"/> must therefore be
    ///     pointed at the same bucket/container the MassTransit side writes to.
    /// </remarks>
    /// <param name="store">
    ///     The claim-check store to load payloads from, pointed at MassTransit's own bucket/container.
    /// </param>
    /// <param name="addressToId">
    ///     Optional override translating a MassTransit repository address into a payload id. Wolverine
    ///     understands the addresses produced by MassTransit's file-system, Amazon S3, and Azure Storage
    ///     repositories out of the box; supply this only for a custom <c>IMessageDataRepository</c> whose
    ///     address format differs.
    /// </param>
    IMassTransitInterop ReadMessageDataFrom(
        Wolverine.Persistence.IClaimCheckStore store,
        Func<Uri, string>? addressToId = null);

    // Newtonsoft.Json variant moved to WolverineFx.Newtonsoft as the
    // UseNewtonsoftForSerialization(this IMassTransitInterop, ...)
    // extension method. Install WolverineFx.Newtonsoft to opt in.
}
