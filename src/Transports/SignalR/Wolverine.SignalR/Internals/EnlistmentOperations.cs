namespace Wolverine.SignalR.Internals;

public static class EnlistmentOperations
{
    /// <summary>
    /// This silly thing just helps track any messages cascaded out from the current
    /// message being handled to the saga id of the current connection to help send
    /// responses back to the originating connection
    /// </summary>
    /// <param name="envelope"></param>
    public static void EnlistInConnectionSaga(Envelope envelope)
    {
        if (envelope is SignalREnvelope se)
        {
            se.SagaId = new WebSocketRouting.Connection(se.ConnectionId).ToString();
        }
    }
}