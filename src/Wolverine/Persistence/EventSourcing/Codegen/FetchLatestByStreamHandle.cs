using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Events;

namespace Wolverine.Persistence.EventSourcing.Codegen;

/// <summary>
///     <c>UpdatedAggregate</c>'s read for an aggregate the handler loaded by a natural key: <c>FetchLatest</c> by the
///     id of the stream the workflow wrote to, read off its <see cref="IEventStream{T}" />.
/// </summary>
/// <remarks>
///     The identity variable of a natural key load is the key, so reading by it as a stream id -- its inner value
///     passed to the Guid or string <c>FetchLatest</c> -- found nothing, or another aggregate, and a <c>TKey?</c>
///     member did not compile. Reading by the key itself is no better: the handler may just have changed it. The
///     stream handle is the one thing that still names the stream after the commit.
/// </remarks>
internal class FetchLatestByStreamHandle : MethodCall
{
    private readonly Type _aggregateType;
    private readonly bool _guidIdentity;

    /// <param name="operationsType">The store's event operations type, which declares <paramref name="fetchLatest" />.</param>
    /// <param name="fetchLatest">The store's <c>FetchLatest&lt;T&gt;(Guid)</c> or <c>FetchLatest&lt;T&gt;(string)</c>, closed over the aggregate.</param>
    /// <param name="guidIdentity">Whether the store identifies streams by Guid, rather than by string.</param>
    public FetchLatestByStreamHandle(Type operationsType, MethodInfo fetchLatest, Type aggregateType,
        bool guidIdentity)
        : base(operationsType, fetchLatest)
    {
        _aggregateType = aggregateType;
        _guidIdentity = guidIdentity;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "MakeGenericType closes IEventStream<TAggregate> at codegen time; AOT consumers pre-generate via TypeLoadMode.Static.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericType closes IEventStream<TAggregate> at codegen time; AOT consumers pre-generate via TypeLoadMode.Static.")]
    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        var streamType = typeof(IEventStream<>).MakeGenericType(_aggregateType);
        var stream = chain.FindVariable(streamType);

        var member = streamType.GetProperty(_guidIdentity
            ? nameof(IEventStream<object>.Id)
            : nameof(IEventStream<object>.Key))!;
        Arguments[0] = new MemberAccessVariable(stream, member);

        yield return stream;

        foreach (var variable in base.FindVariables(chain))
        {
            yield return variable;
        }
    }
}
