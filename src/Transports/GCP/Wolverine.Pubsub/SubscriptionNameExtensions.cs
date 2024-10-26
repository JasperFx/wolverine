namespace Google.Cloud.PubSub.V1;

public static class SubscriptionNameExtensions
{
    public static SubscriptionName WithAssignedNodeNumber(this SubscriptionName subscriptionName,
        int assignedNodeNumber)
    {
        return new SubscriptionName(
            subscriptionName.ProjectId,
            !subscriptionName.SubscriptionId.EndsWith($".{Math.Abs(assignedNodeNumber)}")
                ? $"{subscriptionName.SubscriptionId}.{Math.Abs(assignedNodeNumber)}"
                : subscriptionName.SubscriptionId
        );
    }
}