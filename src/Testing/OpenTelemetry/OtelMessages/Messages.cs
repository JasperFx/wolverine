namespace OtelMessages;

public static class MessagingConstants
{
    public const int Subscriber1Port = 5850;
    public const int WebApiPort = 5851;

    public const string Subscriber1Queue = "otel.subscriber.1";
    public const string Subscriber2Queue = "otel.subscriber.2";

    public const string OtelExchangeName = "otel.exchange";
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

    public string Name { get; set; }
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
    public string Name { get; set; }
}

public class RabbitMessage2
{
    public string Name { get; set; }
}

public class RabbitMessage3
{
    public string Name { get; set; }
}

// Handled in WebApi
public record LocalMessage1(string Name);

// Handled in WebApi
public record LocalMessage2(string Name);

// Handled in Subscriber 3
public record LocalMessage3(string Name);

// Handled in Subscriber 2
public record LocalMessage4(string Name);