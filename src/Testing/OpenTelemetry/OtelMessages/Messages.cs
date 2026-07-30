namespace OtelMessages;

public static class MessagingConstants
{
    public const int Subscriber1Port = 5850;
    public const int WebApiPort = 5851;

    public const string Subscriber1Queue = "otel.subscriber.1";
    public const string Subscriber2Queue = "otel.subscriber.2";

    public const string OtelExchangeName = "otel.exchange";

    // Wolverine stamps this onto Envelope.Source, and TracingTests asserts on it. Shared as a
    // constant because the two had silently disagreed ("WebApi" vs "OtelWebApi") for as long as
    // nothing compiled the test project -- see GH-3704.
    public const string WebApiServiceName = "WebApi";
}

// What's posted to the web api
public class InitialPost
{
    public InitialPost()
    {
    }

    public InitialPost(string name)
    {
        Name = name;
    }

    public string Name { get; set; } = null!;
}

// Turned into a command. Try both invoked and enqueued
public record InitialCommand(string Name);

// Send to subscriber 1
public record TcpMessage1(string Name);

// Sent back to the Web API
public record TcpMessage2(string Name);

// Sent to both Subscriber1 & Subscriber2
public class RabbitMessage1
{
    public string Name { get; set; } = null!;
}

public class RabbitMessage2
{
    public string Name { get; set; } = null!;
}

public class RabbitMessage3
{
    public string Name { get; set; } = null!;
}

// Handled in WebApi
public record LocalMessage1(string Name);

// Handled in WebApi
public record LocalMessage2(string Name);

// Handled in Subscriber 3
public record LocalMessage3(string Name);

// Handled in Subscriber 2
public record LocalMessage4(string Name);