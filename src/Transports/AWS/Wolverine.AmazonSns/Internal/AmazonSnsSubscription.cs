namespace Wolverine.AmazonSns.Internal;

public enum AmazonSnsSubscriptionType
{
    Sqs
}

public class AmazonSnsSubscription
{
    public AmazonSnsSubscription(string endpoint, bool rawMessageDelivery, AmazonSnsSubscriptionType type)
    {
        Endpoint = endpoint;
        Type = type;
        RawMessageDelivery = rawMessageDelivery;
    }

    public string Endpoint { get; }

    public string Protocol =>
        Type switch
        {
            AmazonSnsSubscriptionType.Sqs => "sqs",
            _ => throw new NotImplementedException("Unknown AmazonSnsSubscriptionType")
        };

    public bool RawMessageDelivery { get; set; }
    public AmazonSnsSubscriptionType Type { get; }
}
