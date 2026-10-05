namespace Wolverine.Transports.Sending;

/// <summary>
/// A <see cref="TenantedSender" /> for the case its base class deliberately cannot serve: every sender
/// beneath it is an <see cref="ISenderRequiresCallback" /> and settles the outbox itself.
///
/// <para>
/// GH-4820. <see cref="TenantedSender" /> does not implement <see cref="ISenderRequiresCallback" />, and
/// the comment on it says why — when it did, <c>SendingAgent</c> took the callback-handling path while the
/// fire-and-forget transport senders underneath never called back, so outbox rows were never deleted
/// (GH-2361). That reasoning holds for a <c>RabbitMqSender</c> and does not hold for a
/// <see cref="BatchedSender" />, which exists precisely to report success only once the broker has
/// acknowledged. A transport cannot have it both ways from one type, because the choice is a type test
/// made once when the sending agent is built — hence a second class rather than a flag.
/// </para>
///
/// <para>
/// Use this only when the default sender and every registered tenant sender require a callback. Mixing
/// the two kinds underneath one of these would reintroduce GH-2361 for the fire-and-forget half, so
/// <see cref="RegisterCallback" /> refuses a sender that cannot take one rather than letting that pass
/// quietly.
/// </para>
/// </summary>
public class CallbackAwareTenantedSender : TenantedSender, ISenderRequiresCallback
{
    public CallbackAwareTenantedSender(Uri destination, TenantedIdBehavior tenantedIdBehavior,
        ISender? defaultSender) : base(destination, tenantedIdBehavior, defaultSender)
    {
    }

    /// <summary>
    /// Fans the callback out to the default sender and every registered tenant sender.
    /// </summary>
    public void RegisterCallback(ISenderCallback senderCallback)
    {
        // Every sender is registered by the transport's CreateSender before EndpointCollection asks for
        // the callback, so there is nothing to replay to a sender that arrives later. The two senders
        // senderForTenantId can add on its own afterwards are the default sender, already wired here, and
        // an InvalidTenantSender, which throws rather than sending.
        register(DefaultSender, senderCallback);

        foreach (var pair in TenantSenders())
        {
            register(pair.Value, senderCallback);
        }
    }

    private void register(ISender? sender, ISenderCallback senderCallback)
    {
        if (sender == null) return;

        if (sender is not ISenderRequiresCallback requiresCallback)
        {
            throw new InvalidOperationException(
                $"{nameof(CallbackAwareTenantedSender)} for {Destination} was given a {sender.GetType().Name}, which does not implement {nameof(ISenderRequiresCallback)}. Every sender beneath this one has to settle the outbox itself; a fire-and-forget sender here would never mark its messages successful and the outbox rows would never be deleted. Use {nameof(TenantedSender)} for fire-and-forget senders.");
        }

        requiresCallback.RegisterCallback(senderCallback);
    }
}
