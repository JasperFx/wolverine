namespace Wolverine.AmazonSns.Internal;

public class AmazonSnsSubscriptionAttributes
{
    public string? FilterPolicy  { get; set; }
    
    public string? RedrivePolicy   { get; set; }
    
    /// <summary>
    ///     Enables raw message delivery to Amazon SQS or HTTP/S endpoints
    /// </summary>
    public bool RawMessageDelivery { get; set; }
}
