using System.Diagnostics;
using Wolverine.Transports;

namespace Wolverine.Runtime;

public interface IHandlerPipeline
{
    Task InvokeAsync(Envelope envelope, IChannelCallback channel);
    Task InvokeAsync(Envelope envelope, IChannelCallback channel, Activity activity);
    bool TryDeserializeEnvelope(Envelope envelope, out IContinuation continuation);
}