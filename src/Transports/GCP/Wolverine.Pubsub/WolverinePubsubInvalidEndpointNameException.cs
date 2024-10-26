namespace Wolverine.Pubsub;

public class WolverinePubsubInvalidEndpointNameException : Exception
{
    public WolverinePubsubInvalidEndpointNameException(string topicName, string? message = null,
        Exception? innerException = null) : base(
        message ?? $"Google Cloud Platform Pub/Sub endpoint name \"{topicName}\" is invalid.", innerException)
    {
    }
}