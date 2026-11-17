using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine;

/// <summary>
///     GH-5572. Keep completed instances of this saga type for a while instead of deleting them, so they can still be
///     read and queried. The period is the sum of <see cref="Days" />, <see cref="Hours" /> and <see cref="Minutes" />,
///     and it wins over <c>opts.Policies.RetainCompletedSagas()</c> for this saga type.
/// </summary>
/// <remarks>
///     A retained saga is marked completed (<see cref="Saga.CompletedAt" />), and a later message for it is treated as
///     a message for a saga that does not exist: it goes to <c>NotFound</c>, never to <c>Handle</c>. A node agent
///     deletes retained sagas once they are older than the period. The saga's storage has to support retention: the
///     lightweight database tables, Marten, Polecat, Fisher, EF Core, RavenDB and CosmosDB do. Sagas kept in memory are
///     deleted as before, and any other storage refuses the attribute at startup.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public class RetainWhenCompletedAttribute : Attribute
{
    public int Days { get; set; }
    public int Hours { get; set; }
    public int Minutes { get; set; }

    /// <summary>
    ///     The retention period
    /// </summary>
    public TimeSpan Retention => TimeSpan.FromDays(Days) + TimeSpan.FromHours(Hours) + TimeSpan.FromMinutes(Minutes);
}

/// <summary>
///     GH-5572. Which saga types keep their completed instances, and for how long
/// </summary>
public static class SagaRetention
{
    /// <summary>
    ///     The period set on the saga type itself with <see cref="RetainWhenCompletedAttribute" />, if any
    /// </summary>
    public static TimeSpan? ExplicitFor(Type sagaType)
    {
        var attribute = sagaType.GetAttribute<RetainWhenCompletedAttribute>();
        if (attribute == null) return null;

        var retention = attribute.Retention;
        if (retention <= TimeSpan.Zero)
        {
            throw new InvalidSagaException(
                $"[{nameof(RetainWhenCompletedAttribute)}] on saga type {sagaType.FullNameInCode()} needs a positive retention period: set Days, Hours or Minutes");
        }

        return retention;
    }

    /// <summary>
    ///     How long completed instances of <paramref name="sagaType" /> are kept: the saga type's own
    ///     <see cref="RetainWhenCompletedAttribute" />, else <see cref="DurabilitySettings.CompletedSagaRetention" />.
    ///     Null when they are deleted on completion. This does not check whether the saga's storage supports
    ///     retention.
    /// </summary>
    public static TimeSpan? For(Type sagaType, WolverineOptions options)
    {
        return ExplicitFor(sagaType) ?? options.Durability.CompletedSagaRetention;
    }

    /// <summary>
    ///     Refuses a saga type that keeps its completed instances (<see cref="RetainWhenCompletedAttribute" /> or
    ///     <c>opts.Policies.RetainCompletedSagas()</c>) but declares its own <c>CompletedAt</c>. That member hides
    ///     <see cref="Saga.CompletedAt" />, which is what marks a retained saga completed. Says nothing when retention
    ///     is off for the saga type.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Saga types come from handler discovery, which roots them. A trimmed member could not hide CompletedAt anyway.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Walks the saga type's base classes up to Saga. Saga types come from handler discovery, which roots them.")]
    public static void AssertCompletedAtIsNotHidden(Type sagaType, WolverineOptions? options)
    {
        var retention = ExplicitFor(sagaType) ?? options?.Durability.CompletedSagaRetention;
        if (retention == null) return;

        for (var type = sagaType; type != null && type != typeof(Saga); type = type.BaseType)
        {
            var hides = type.GetMember(nameof(Saga.CompletedAt),
                    MemberTypes.Property | MemberTypes.Field,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .Length > 0;

            if (hides)
            {
                throw new InvalidSagaException(
                    $"Saga type {sagaType.FullNameInCode()} keeps its completed instances, but {type.FullNameInCode()} declares its own {nameof(Saga.CompletedAt)}, which hides {nameof(Saga)}.{nameof(Saga.CompletedAt)}. Remove or rename it.");
            }
        }
    }

    /// <summary>
    ///     Called by generated code when a saga of a retained type completes, right before the saga is saved. Marks it
    ///     completed with the current time. Not meant to be called from application code.
    /// </summary>
    /// <remarks>
    ///     The whole saga is saved as it stands, so any state the saga carries, such as a status or a log of what it
    ///     did, is kept with it while it is retained. This is the one place a retained saga is marked, so a later
    ///     completion style (a compensated saga, say) can add to it here.
    /// </remarks>
    public static void MarkCompleted(Saga saga)
    {
        saga.CompletedAt ??= DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Called by generated code when a message that would start a saga arrives for the id of a retained saga that
    ///     has already completed, and the saga has no <c>NotFound</c> method for the message. The message is not
    ///     handled. Not meant to be called from application code.
    /// </summary>
    public static void SkipStartOfCompletedSaga(MessageContext context, Type sagaType, object sagaId)
    {
        context.Runtime.Logger.LogInformation(
            "Did not start saga {SagaType} with id {SagaId} for message {MessageType}: a completed saga with that id is still retained",
            sagaType.FullNameInCode(), sagaId, context.Envelope?.Message?.GetType().FullNameInCode());
    }
}
