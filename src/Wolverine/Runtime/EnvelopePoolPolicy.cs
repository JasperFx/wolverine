using Microsoft.Extensions.ObjectPool;

namespace Wolverine.Runtime;

/// <summary>
/// <see cref="ObjectPool{T}"/> policy for <see cref="Envelope"/> instances
/// owned by the internal receive pipeline (see wolverine#2726).
///
/// Lives as a separate type — and not as another <c>partial class WolverineRuntime</c>
/// the way <see cref="WolverineRuntime"/> hosts the <see cref="MessageContext"/>
/// pool policy — because <see cref="WolverineRuntime"/> already inherits
/// <see cref="PooledObjectPolicy{T}"/> closed over <see cref="MessageContext"/>,
/// and C# doesn't allow a class to declare two base-class instantiations of
/// the same open generic.
///
/// <see cref="Return"/> calls <see cref="Envelope.Reset"/> on the way back to
/// the pool; the consumer that <c>Get()</c>s an envelope is responsible for
/// re-stamping <see cref="Envelope.Id"/> / <see cref="Envelope.SentAt"/> /
/// the message before use — Reset deliberately zeroes both, see its doc-comment.
///
/// Always returns <c>true</c> from <see cref="Return"/>: the pool itself caps
/// retention size, so unconditional return is correct here.
/// </summary>
internal sealed class EnvelopePoolPolicy : PooledObjectPolicy<Envelope>
{
    public override Envelope Create()
    {
        return new Envelope();
    }

    public override bool Return(Envelope envelope)
    {
        envelope.Reset();
        return true;
    }
}
