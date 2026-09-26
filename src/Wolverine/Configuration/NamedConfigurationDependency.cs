using Microsoft.Extensions.Configuration;

namespace Wolverine.Configuration;

/// <summary>
/// GH-4527. A connection string name that some part of this Wolverine application expects to find in
/// <see cref="IConfiguration"/> -- registered by APIs like <c>UseRabbitMqUsingNamedConnection(name)</c> and
/// <c>UseKafkaUsingNamedConnection(name)</c> so the runtime can validate every one of them up front, in one
/// pass, before any transport connects or the message store migrates.
///
/// <para>
/// Before this existed each name was validated lazily, inside the DI factory that consumed it, which meant a
/// host with two missing Aspire references failed twice on two deploys -- and failed only after persistence
/// had already migrated and any earlier transport had already connected.
/// </para>
/// </summary>
/// <param name="Kind">What needs it, for the message -- e.g. "Rabbit MQ", "Kafka".</param>
/// <param name="Name">The connection string key, as passed to <see cref="ConfigurationExtensions.GetConnectionString"/>.</param>
/// <param name="AspireResourceMethod">
/// The Aspire AppHost builder method that would register this resource, e.g. <c>AddRabbitMQ</c>. Used to spell
/// out the remedy, because a missing Aspire reference is what produces this failure nine times out of ten.
/// Null when the dependency has no Aspire equivalent worth naming.
/// </param>
public record NamedConfigurationDependency(string Kind, string Name, string? AspireResourceMethod = null);
