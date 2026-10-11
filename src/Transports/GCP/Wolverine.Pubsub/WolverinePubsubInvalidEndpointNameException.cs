namespace Wolverine.Pubsub;

public class WolverinePubsubInvalidEndpointNameException : Exception
{
    /// <summary>
    /// The rule a Pub/Sub topic or subscription name has to satisfy, as <c>PubsubTransport.NameRegex</c> enforces it
    /// </summary>
    public const string NamingRule =
        "A Google Cloud Pub/Sub topic or subscription name must start with a letter, be 3 to 255 characters long, " +
        "contain only letters, numbers, dashes (-), underscores (_), periods (.), tildes (~), pluses (+) or percent signs (%), " +
        "and must not start with \"goog\".";

    public WolverinePubsubInvalidEndpointNameException(string topicName, string? message = null,
        Exception? innerException = null) : base(
        message ?? $"Google Cloud Platform Pub/Sub endpoint name \"{topicName}\" is invalid. {NamingRule}", innerException)
    {
    }
}
