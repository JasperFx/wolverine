using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Events;
using Wolverine.Configuration;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     Read a stream's raw events as <c>IReadOnlyList&lt;IEvent&gt;</c>. GH-3627.
/// </summary>
/// <remarks>
///     <para>
///     The companion to <see cref="StreamStateAttribute" /> for timeline and audit shaped handlers.
///     Batched with any other batchable load on the same handler into a single round trip by the store.
///     </para>
///     <para>
///     <b>There is deliberately no <c>Required</c> here.</b> A missing stream yields an <i>empty list</i>,
///     not null, so the null-guard model the rest of the <c>IDataRequirement</c> family is built on has
///     nothing to test — a guard would either never fire or would have to invent a count threshold, and
///     "zero events" is not the same question as "no such stream". Pair with
///     <c>[StreamState] StreamState state</c> when the handler needs an existence or aggregate-type guard;
///     that reads the same stream in the same batch and answers the question precisely.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public class StreamEventsAttribute : StreamReadAttribute
{
    public StreamEventsAttribute()
    {
    }

    public StreamEventsAttribute(string argumentName) : base(argumentName)
    {
    }

    protected override Type ReadType => typeof(IReadOnlyList<IEvent>);

    protected override Frame BuildFrame(IEventSourcingFrameProvider provider, Variable identity)
        => provider.BuildFetchStreamFrame(identity);

    protected override Variable ModifyForRead(IChain chain, ParameterInfo parameter, Frame frame, Variable created,
        Variable identity)
    {
        chain.Middleware.Add(frame);
        return created;
    }
}
