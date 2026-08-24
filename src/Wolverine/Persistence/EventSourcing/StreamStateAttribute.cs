using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Events;
using Wolverine.Configuration;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     Read a stream's <see cref="StreamState" /> — version, aggregate type, created/updated timestamps —
///     without folding it into an aggregate. GH-3627.
/// </summary>
/// <remarks>
///     For handlers that serve the event history itself, where <see cref="ReadModelAttribute" /> cannot
///     express the read because state has already collapsed what they need. Batched with any other
///     batchable load on the same handler into a single round trip by the store, exactly as
///     <c>[ReadModel]</c> is.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public class StreamStateAttribute : StreamReadAttribute, IDataRequirement
{
    private OnMissing? _onMissing;
    private bool? _required;

    public StreamStateAttribute()
    {
    }

    public StreamStateAttribute(string argumentName) : base(argumentName)
    {
    }

    /// <summary>
    ///     Should Wolverine stop the handler when the stream does not exist? Defaults to the opposite of the
    ///     parameter's nullable annotation, matching <see cref="ReadModelAttribute" />: <c>StreamState state</c>
    ///     is required, <c>StreamState? state</c> is not.
    /// </summary>
    public bool Required
    {
        get => _required ?? true;
        set => _required = value;
    }

    public OnMissing OnMissing
    {
        get => _onMissing ?? OnMissing.Simple404;
        set => _onMissing = value;
    }

    public string MissingMessage { get; set; } = null!;

    protected override Type ReadType => typeof(StreamState);

    protected override Frame BuildFrame(IEventSourcingFrameProvider provider, Variable identity)
        => provider.BuildFetchStreamStateFrame(identity);

    protected override Variable ModifyForRead(IChain chain, ParameterInfo parameter, Frame frame, Variable created,
        Variable identity)
    {
        // Nullable annotation decides the default, same as [ReadModel] since GH-3929
        _required ??= !ParameterNullability.IsNullableAnnotated(parameter);

        if (chain.IsDataRequired(this))
        {
            var otherFrames = chain.AddStopConditionIfNull(created, identity, this);
            var block = new LoadEntityFrameBlock(created, otherFrames);
            chain.Middleware.Add(block);

            return block.Mirror;
        }

        chain.Middleware.Add(frame);
        return created;
    }
}
