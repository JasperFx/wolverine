namespace Wolverine.Pubsub;

public class WolverinePubsubTransportNotConnectedException : Exception
{
    public WolverinePubsubTransportNotConnectedException(
        string message = "Google Cloud Platform Pub/Sub transport has not been connected",
        Exception? innerException = null) : base(message, innerException)
    {
    }
}