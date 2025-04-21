using Amazon.SimpleNotificationService.Model;

namespace Wolverine.AmazonSns.Internal;

public enum AmazonSnsSubscriptionType
{
    Sqs
}

public class AmazonSnsSubscription
{
    public AmazonSnsSubscription(string endpoint, AmazonSnsSubscriptionType type,
        AmazonSnsSubscriptionAttributes attributes)
    {
        Endpoint = endpoint;
        Type = type;
        Attributes = attributes;
    }

    internal AmazonSnsSubscription(Subscription subscription)
    {
        Endpoint = subscription.Endpoint;
        SubscriptionArn = subscription.SubscriptionArn;
        Attributes = new AmazonSnsSubscriptionAttributes();
    }

    public string? SubscriptionArn { get; set; }

    public string Endpoint { get; }
    public AmazonSnsSubscriptionType Type { get; }

    public string Protocol =>
        Type switch
        {
            AmazonSnsSubscriptionType.Sqs => "sqs",
            _ => throw new NotImplementedException("Unknown AmazonSnsSubscriptionType")
        };

    public AmazonSnsSubscriptionAttributes Attributes { get; }
}
