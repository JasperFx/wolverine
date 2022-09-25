using System;
using Wolverine.Util.Dataflow;

namespace Wolverine.Logging;

internal class StatusWaiter : ConditionalWaiter<ListenerState>, IObserver<ListenerState>
{

    private readonly ListenerState _expected;

    public StatusWaiter(ListenerState expected, IObservable<ListenerState> parent, TimeSpan timeout) : base(parent, timeout)
    {
        _expected = expected;
    }

    protected override bool hasCompleted(ListenerState state)
    {
        return (state.EndpointName == _expected.EndpointName || state.Uri == _expected.Uri) && state.Status == _expected.Status;
    }


}