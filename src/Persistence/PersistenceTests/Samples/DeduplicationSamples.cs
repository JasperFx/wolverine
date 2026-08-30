using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence;
using Wolverine.Postgresql;

namespace PersistenceTests.Samples;

public record RebuildProjection(string ProjectionName, DateTimeOffset OccurrenceUtc);

#region sample_deduplication_identity_on_a_member

// The message type declares its own logical identity once, and every publisher
// gets it -- no DeliveryOptions at any call site
public record ArchiveInvoice([property: DeduplicationIdentity] string InvoiceNumber, DateOnly AsOf);

#endregion

#region sample_deduplication_identity_naming_a_member

// The same thing for a contract whose members you cannot decorate
[DeduplicationIdentity(nameof(ReceiveShipment.ShipmentId))]
public record ReceiveShipment(Guid ShipmentId, string Warehouse);

#endregion

public record CreateOrder(string Sku, int Quantity);

public interface ICreateCommand;

public static class DeduplicationSamples
{
    public static async Task bootstrapping()
    {
        #region sample_enabling_message_deduplication

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql("connection string");

                // Opt in to logical message deduplication. This provisions a new
                // "wolverine_deduplication" table -- nothing else about your message
                // storage changes, and leaving this off means no schema migration at all
                opts.Durability.EnableMessageDeduplication = true;

                // How long a logical id is honoured before the reaper removes it.
                // The default is 24 hours. This IS the guarantee, so size it against
                // how long a duplicate could plausibly arrive
                opts.Durability.DeduplicationWindow = 24.Hours();
            }).StartAsync();

        #endregion
    }

    #region sample_deduplicated_message_handler

    public static class RebuildProjectionHandler
    {
        // Wolverine will refuse to run this handler twice for the same
        // Envelope.DeduplicationId within the deduplication window
        [Deduplicated]
        public static void Handle(RebuildProjection command)
        {
            // rebuild the projection...
        }
    }

    #endregion

    #region sample_publishing_with_a_deduplication_id

    public static ValueTask ScheduleNightlyRebuild(IMessageBus bus, string projectionName, DateTimeOffset occurrence)
    {
        return bus.PublishAsync(new RebuildProjection(projectionName, occurrence), new DeliveryOptions
        {
            // The logical identity of the WORK, not of this particular delivery.
            // An operator double-click, a console republish, and an agent that
            // pre-published this occurrence yesterday all produce this same id
            DeduplicationId = $"{projectionName}|{occurrence:O}"
        });
    }

    #endregion

    #region sample_deduplicated_from_the_message_body

    public static class CreateOrderHandler
    {
        // Derive the logical id from a member of the message itself rather than
        // asking every publisher to set DeliveryOptions
        [Deduplicated(ValueSource.InputMember, nameof(CreateOrder.Sku))]
        public static void Handle(CreateOrder command)
        {
            // create the order...
        }
    }

    #endregion

    public static async Task deriving_the_id_from_the_message()
    {
        #region sample_deriving_deduplication_ids

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql("connection string");
                opts.Durability.EnableMessageDeduplication = true;

                // Compose the logical id from more than one member, or from anything
                // else you can reach from the message
                opts.MessageDeduplication.ByMessage<RebuildProjection>(
                    x => $"{x.ProjectionName}|{x.OccurrenceUtc:O}");

                // Or, for generated message types you can neither decorate nor be
                // bothered writing a lambda for, use the first member that matches
                // one of these names
                opts.MessageDeduplication.ByMemberNamed("IdempotencyKey", "DeduplicationId");

                // Same thing as ByMessage<T>(), reached through the message type policies
                opts.Policies.ForMessagesOfType<CreateOrder>()
                    .DeduplicateBy(x => $"{x.Sku}|{x.Quantity}");
            }).StartAsync();

        #endregion
    }

    public static async Task blanket_policy()
    {
        #region sample_requiring_deduplication_ids_by_policy

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql("connection string");
                opts.Durability.EnableMessageDeduplication = true;

                // Apply logical deduplication to every handler matching a filter, instead
                // of decorating each one. Useful when the rule is "every create-style
                // command is deduplicated" and some of the handlers are not yours
                opts.Policies.RequireDeduplicationId(chain =>
                    chain.MessageType.CanBeCastTo<ICreateCommand>());
            }).StartAsync();

        #endregion
    }

    #region sample_deduplication_with_optional_id

    public static class MixedTrafficHandler
    {
        // Some publishers set a logical id and some do not. Those that do are
        // protected; those that do not are handled exactly as if the feature
        // were off, and pay no database round trip
        [Deduplicated(Required = false)]
        public static void Handle(CreateOrder command)
        {
            // ...
        }
    }

    #endregion
}
