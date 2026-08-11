using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
/// Appends a handler's returned event, or collection of events, onto the aggregate's event stream.
/// Shared by every event sourcing store integration — see GH-3907.
/// </summary>
/// <remarks>
/// Both store copies of this type were byte-identical apart from an unused <c>using</c> of the store's
/// own <c>Events</c> namespace: everything here is expressed over <see cref="IEventStream{T}"/> from
/// JasperFx.Events, so there was never a store dependency to break.
///
/// <para>
/// The constraint is widened from the copies' <c>class</c> to <c>notnull</c>, which is what
/// <see cref="IEventStream{T}"/> itself declares. This frame is closed reflectively over the aggregate
/// type, and a <c>class</c> constraint — unlike <c>notnull</c> — is enforced at <c>MakeGenericType</c>
/// time, so the narrower form would throw for a struct aggregate rather than generate the same correct
/// code. Strictly wider: nothing that works today changes.
/// </para>
/// </remarks>
internal class RegisterEventsFrame<T> : MethodCall where T : notnull
{
    public RegisterEventsFrame(Variable returnVariable) : base(typeof(IEventStream<T>),
        FindMethod(returnVariable.VariableType))
    {
        Arguments[0] = returnVariable;
        CommentText = "Capturing any possible events returned from the command handlers";
    }

    internal static MethodInfo FindMethod(Type responseType)
    {
        return responseType.CanBeCastTo<IEnumerable<object>>()
            ? ReflectionHelper.GetMethod<IEventStream<T>>(x => x.AppendMany(new List<object>()))!
            : ReflectionHelper.GetMethod<IEventStream<T>>(x => x.AppendOne(null!))!;
    }
}
