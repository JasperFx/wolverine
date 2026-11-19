using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Core.Reflection;
using JasperFx.Events;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// GH-5415. The event operations of a store's session, reached store-agnostically. No JasperFx session abstraction
/// declares the <c>Events</c> accessor -- each store surfaces its own <c>Events</c> property of its own type -- but
/// Marten's, Polecat's and Fisher's all implement <see cref="IQueryEventStore" />, and a writable session's
/// <see cref="IEventStoreOperations" />
/// </summary>
internal static class EventSessionAccess
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> _events = new();

    public static IQueryEventStore QueryEventsOf(object session)
        => session as IQueryEventStore ?? eventsOf(session) as IQueryEventStore
           ?? throw new NotSupportedException(
               $"Session type {session.GetType().FullNameInCode()} exposes no JasperFx.Events.IQueryEventStore through an 'Events' member.");

    public static IEventStoreOperations EventOperationsOf(object session)
        => session as IEventStoreOperations ?? eventsOf(session) as IEventStoreOperations
           ?? throw new NotSupportedException(
               $"Session type {session.GetType().FullNameInCode()} exposes no JasperFx.Events.IEventStoreOperations through an 'Events' member.");

    private static object? eventsOf(object session)
        => _events.GetOrAdd(session.GetType(), eventsProperty)?.GetValue(session);

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Reads the public Events property of a store's own session type, which every store declares.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Reads the public Events property of a store's own session type, which every store declares.")]
    private static PropertyInfo? eventsProperty(Type sessionType)
    {
        // The most derived one: a session type may hide its base type's Events with its own
        for (var type = sessionType; type != null; type = type.BaseType)
        {
            var property = type.GetProperty("Events",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property != null) return property;
        }

        return null;
    }

    /// <summary>The stream identity of an event-sourced saga: a Guid or a string, unwrapped from a strong-typed id</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Strong-typed id shape of a saga id type rooted by the saga.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Strong-typed id shape of a saga id type rooted by the saga.")]
    public static object StreamIdentityOf(object identity)
    {
        if (identity is Guid or string) return identity;

        try
        {
            return ValueTypeInfo.ForType(identity.GetType()).ValueProperty.GetValue(identity) ?? identity;
        }
        catch
        {
            return identity;
        }
    }
}
